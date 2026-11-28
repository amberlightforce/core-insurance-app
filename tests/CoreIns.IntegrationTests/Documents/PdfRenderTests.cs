using System.Net;
using System.Security.Cryptography;
using System.Text;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Documents;

/// <summary>BO-DOC-07: PDF rendition through a fake Gotenberg (in-process HTTP listener) and through the plain local fallback.</summary>
public sealed class PdfRenderTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string Admin = "Platform.Admin";

    private static async Task<(string DocumentId, string ArchiveId)> RequestAsync(HttpClient client, string marker)
    {
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", new
        {
            documentType = "DT-REFUND-ADVICE",
            subject = new { module = "BIL", type = "DelinquencyCase", id = $"case-{marker}" },
            recipients = new[] { new { role = "ADDRESSEE", partyId = Guid.CreateVersion7() } },
            deliveryInstruction = new { mode = "EXPLICIT" },
            payloadSupplement = new { requestNo = $"PDF-{marker}", placeholders = new { customerName = "Μαρία", policyNo = "POL000000123", refundAmount = "55.00 EUR" } },
        }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var id = body.Text("documentId");
        var (_, doc) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/{id}", roles: Admin);
        return (id, doc.Text("archiveId"));
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, string archiveId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/doc/v1/archives/{archiveId}/content", UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Admin);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gotenberg_pdf_is_stored_as_a_pdfa_rendition_and_a_repeat_returns_it()
    {
        using var fake = new FakeGotenberg(status: HttpStatusCode.OK);
        await using var factory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Documents:GotenbergUrl"] = fake.Url });
        using var client = factory.CreateClient();
        var (documentId, archiveId) = await RequestAsync(client, Guid.NewGuid().ToString("N")[..8]);

        var (first, body) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/render-pdf", new { documentId }, roles: Admin);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["pdfA"]!.GetValue<bool>().ShouldBeTrue();
        body!["alreadyRendered"]!.GetValue<bool>().ShouldBeFalse();
        var pdfId = body.Text("archiveId");
        var bytes = await DownloadAsync(client, pdfId);
        Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("%PDF");
        Convert.ToHexStringLower(SHA256.HashData(bytes)).ShouldBe(body.Text("hash"));
        fake.LastBody.ShouldContain("pdfa");
        fake.LastBody.ShouldContain("PDF/A-3b");
        fake.LastBody.ShouldContain("Μαρία");

        // The HTML entry is untouched and still verifies.
        var (_, verify) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/verify", new { archiveId }, roles: Admin);
        verify.Text("result").ShouldBe("OK");

        var (second, again) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/render-pdf", new { documentId }, roles: Admin);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        again!["alreadyRendered"]!.GetValue<bool>().ShouldBeTrue();
        again.Text("archiveId").ShouldBe(pdfId);
        fake.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Without_gotenberg_a_plain_pdf_flagged_not_pdfa_is_produced()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString);
        using var client = factory.CreateClient();
        var (documentId, _) = await RequestAsync(client, Guid.NewGuid().ToString("N")[..8]);

        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/render-pdf", new { documentId }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["pdfA"]!.GetValue<bool>().ShouldBeFalse();
        var bytes = await DownloadAsync(client, body.Text("archiveId"));
        Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("%PDF-");
        Encoding.ASCII.GetString(bytes).ShouldContain("POL000000123");
    }

    [Fact]
    public async Task A_failing_renderer_answers_502_and_stores_nothing_and_unknown_or_forbidden_are_refused()
    {
        using var fake = new FakeGotenberg(status: HttpStatusCode.InternalServerError);
        await using var factory = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Documents:GotenbergUrl"] = fake.Url });
        using var client = factory.CreateClient();
        var (documentId, _) = await RequestAsync(client, Guid.NewGuid().ToString("N")[..8]);

        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/render-pdf", new { documentId }, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/render-pdf", new { documentId = Guid.NewGuid() }, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/render-pdf", new { documentId }, roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>A tiny stand-in for Gotenberg's Chromium route that records the multipart body and answers a fake PDF.</summary>
    private sealed class FakeGotenberg : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly HttpStatusCode _status;
        private int _calls;

        public FakeGotenberg(HttpStatusCode status)
        {
            _status = status;
            var port = System.Net.Sockets.TcpListener.Create(0);
            port.Start();
            var number = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            Url = $"http://127.0.0.1:{number}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public string LastBody { get; private set; } = string.Empty;

        public int Calls => Volatile.Read(ref _calls);

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    LastBody = await reader.ReadToEndAsync();
                    Interlocked.Increment(ref _calls);
                    context.Response.StatusCode = (int)_status;
                    var pdf = "%PDF-1.7 fake gotenberg output"u8.ToArray();
                    await context.Response.OutputStream.WriteAsync(pdf);
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
        }

        public void Dispose() => _listener.Close();
    }
}
