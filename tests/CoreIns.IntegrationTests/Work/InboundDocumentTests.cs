using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using CoreIns.Modules.Work.Inbound;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace CoreIns.IntegrationTests.Work;

/// <summary>
/// BO-WRK-11: inbound documents: intake with a malware scan (infected never reaches CLEAN, an unavailable scanner leaves the file quarantined,
/// no scanner refuses the intake), manual keying, split, link, reject, redact (the original stays under hold) and export.
/// </summary>
public sealed class InboundDocumentTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Clerk = "Staff.Underwriter";
    private const string Manager = "Staff.UnderwritingManager";
    private const string Base = "/api/wrk/v1/inbound-documents";

    private readonly ApiHostFactory _factory = new(database.AppConnectionString);
    private HttpClient? _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _client ??= _factory.CreateClient();

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_clean_file_is_stored_scanned_and_listed_and_a_clerk_keys_it_by_hand()
    {
        var obj = $"POL/Policy/{Guid.NewGuid()}";
        var submitted = await SubmitAsync(Client, [("claim-form.pdf", Pdf("hello"))], link: obj);
        submitted.Response.StatusCode.ShouldBe(HttpStatusCode.OK, submitted.Body?.ToJsonString());
        var id = submitted.Body!["documentIds"]![0]!.GetValue<string>();
        submitted.Body["inboundNumbers"]![0]!.GetValue<string>().ShouldStartWith("INB-");

        var got = await SendAsync(Client, HttpMethod.Get, $"{Base}/{id}", null, Clerk);
        var doc = got.Body!["document"]!;
        doc["state"]!.GetValue<string>().ShouldBe("CLEAN");
        doc["scanVerdict"]!.GetValue<string>().ShouldBe("CLEAN");
        doc["objectRef"]!.GetValue<string>().ShouldBe(obj);
        var archiveId = doc["archiveId"]!.GetValue<string>();
        (await ScalarAsync($"SELECT count(*) FROM doc.archive WHERE archive_id = '{archiveId}'")).ShouldBe(1L);

        var listed = await SendAsync(Client, HttpMethod.Get, $"{Base}?state=CLEAN&objectRef={obj}", null, Clerk);
        listed.Body!["items"]!.AsArray().Count.ShouldBe(1);

        // verify needs the class and the keyed data.
        (await SendAsync(Client, HttpMethod.Post, Base + "/verify", new { id }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var verified = await SendAsync(Client, HttpMethod.Post, Base + "/verify", new { id, documentClass = "CLAIM_FORM", data = new { claimNo = "CLM-1", amount = "120.00" } }, Clerk);
        verified.Response.StatusCode.ShouldBe(HttpStatusCode.OK, verified.Body?.ToJsonString());
        verified.Body!["document"]!["state"]!.GetValue<string>().ShouldBe("VERIFIED");
        verified.Body["document"]!["keyedData"]!["claimNo"]!.GetValue<string>().ShouldBe("CLM-1");

        // Verified documents cannot be rejected any more.
        (await SendAsync(Client, HttpMethod.Post, Base + "/reject", new { id, reason = "late" }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_infected_file_never_reaches_clean_and_its_bytes_are_not_stored()
    {
        var submitted = await SubmitAsync(Client, [("note.txt", Encoding.ASCII.GetBytes(StubMalwareScanner.Eicar))]);
        submitted.Response.StatusCode.ShouldBe(HttpStatusCode.OK, submitted.Body?.ToJsonString());
        var id = submitted.Body!["documentIds"]![0]!.GetValue<string>();
        var doc = (await SendAsync(Client, HttpMethod.Get, $"{Base}/{id}", null, Clerk)).Body!["document"]!;
        doc["state"]!.GetValue<string>().ShouldBe("INFECTED");
        doc["scanVerdict"]!.GetValue<string>().ShouldBe("INFECTED");
        doc["archiveId"].ShouldBeNull();

        (await SendAsync(Client, HttpMethod.Post, Base + "/verify", new { id, documentClass = "X", data = new { } }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(Client, HttpMethod.Post, Base + "/reprocess", new { id }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The database refuses it as well, even for the owner role.
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await Should.ThrowAsync<PostgresException>(async () =>
            await dataSource.CreateCommand($"UPDATE wrk.inbound_document SET state = 'CLEAN' WHERE inbound_document_id = '{id}'").ExecuteNonQueryAsync(Ct));

        // It can only be rejected.
        (await SendAsync(Client, HttpMethod.Post, Base + "/reject", new { id, reason = "malware" }, Clerk)).Body!["document"]!["state"]!.GetValue<string>().ShouldBe("REJECTED");
    }

    [Fact]
    public async Task An_unavailable_scanner_leaves_the_file_quarantined_until_a_reprocess_finds_it_clean()
    {
        var scanner = new SwitchScanner(ScanVerdict.Unavailable);
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IMalwareScanner>(scanner))));
        using var client = factory.CreateClient();
        var id = (await SubmitAsync(client, [("scan.pdf", Pdf("later"))])).Body!["documentIds"]![0]!.GetValue<string>();
        var doc = (await SendAsync(client, HttpMethod.Get, $"{Base}/{id}", null, Clerk)).Body!["document"]!;
        doc["state"]!.GetValue<string>().ShouldBe("QUARANTINED");
        doc["scanVerdict"]!.GetValue<string>().ShouldBe("UNAVAILABLE");
        (await SendAsync(client, HttpMethod.Post, Base + "/verify", new { id, documentClass = "X", data = new { } }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        scanner.Verdict = ScanVerdict.Clean;
        var again = await SendAsync(client, HttpMethod.Post, Base + "/reprocess", new { id }, Clerk);
        again.Response.StatusCode.ShouldBe(HttpStatusCode.OK, again.Body?.ToJsonString());
        again.Body!["document"]!["state"]!.GetValue<string>().ShouldBe("CLEAN");
    }

    [Fact]
    public async Task Without_a_bound_scanner_the_intake_is_refused()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IMalwareScanner>()));
        using var client = factory.CreateClient();
        var refused = await SubmitAsync(client, [("a.pdf", Pdf("x"))]);
        refused.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Body!.ToJsonString().ShouldContain("CAPABILITY-OFF");
    }

    [Fact]
    public async Task File_types_and_sizes_are_checked_before_anything_is_stored()
    {
        (await SubmitAsync(Client, [("run.exe", Encoding.ASCII.GetBytes("MZ"))])).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        // The content must match the extension.
        var lie = await SubmitAsync(Client, [("fake.pdf", Encoding.ASCII.GetBytes("not a pdf"))]);
        lie.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        lie.Body!.ToJsonString().ShouldContain("FILE-TYPE");
        var big = await SubmitAsync(Client, [("big.pdf", Pdf(new string('a', 5 * 1024 * 1024 + 10)))]);
        big.Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        big.Body!.ToJsonString().ShouldContain("FILE-SIZE");
    }

    [Fact]
    public async Task Split_link_relink_and_reject_follow_the_state_rules()
    {
        var id = (await SubmitAsync(Client, [("bundle.pdf", Pdf("bundle"))])).Body!["documentIds"]![0]!.GetValue<string>();
        var obj = $"CLM/Claim/{Guid.NewGuid()}";
        var other = $"CLM/Claim/{Guid.NewGuid()}";
        (await SendAsync(Client, HttpMethod.Post, Base + "/link", new { id, objectRef = obj }, Clerk)).Body!["document"]!["objectRef"]!.GetValue<string>().ShouldBe(obj);
        (await SendAsync(Client, HttpMethod.Post, Base + "/link", new { id, objectRef = other }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(Client, HttpMethod.Post, Base + "/relink", new { id, objectRef = other, reason = "wrong claim" }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var relinked = await SendAsync(Client, HttpMethod.Post, Base + "/relink", new { id, objectRef = other, reason = "wrong claim" }, Manager);
        relinked.Body!["document"]!["objectRef"]!.GetValue<string>().ShouldBe(other);

        (await SendAsync(Client, HttpMethod.Post, Base + "/split", new { id, parts = new[] { new { pageRange = "1-2" } } }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var split = await SendAsync(Client, HttpMethod.Post, Base + "/split",
            new { id, parts = new[] { new { pageRange = "1-2", documentClass = "INVOICE" }, new { pageRange = "3", documentClass = "PHOTO" } } }, Clerk);
        split.Response.StatusCode.ShouldBe(HttpStatusCode.OK, split.Body?.ToJsonString());
        split.Body!["document"]!["parent"]!["state"]!.GetValue<string>().ShouldBe("SPLIT");
        var children = split.Body["document"]!["children"]!.AsArray();
        children.Count.ShouldBe(2);
        children.All(c => c!["state"]!.GetValue<string>() == "CLEAN" && c["parentId"]!.GetValue<string>() == id).ShouldBeTrue();
        (await SendAsync(Client, HttpMethod.Post, Base + "/reject", new { id, reason = "x" }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var childId = children[0]!["id"]!.GetValue<string>();
        (await SendAsync(Client, HttpMethod.Post, Base + "/reject", new { id = childId }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(Client, HttpMethod.Post, Base + "/reject", new { id = childId, reason = "duplicate" }, Clerk)).Body!["document"]!["state"]!.GetValue<string>().ShouldBe("REJECTED");
    }

    [Fact]
    public async Task Redact_keeps_the_original_under_hold_and_export_is_for_managers()
    {
        var obj = $"POL/Policy/{Guid.NewGuid()}";
        var id = (await SubmitAsync(Client, [("id-card.pdf", Pdf("secret"))], link: obj)).Body!["documentIds"]![0]!.GetValue<string>();
        var original = (await SendAsync(Client, HttpMethod.Get, $"{Base}/{id}", null, Clerk)).Body!["document"]!["archiveId"]!.GetValue<string>();

        var redactBody = new { id, reason = "third-party data", content = Convert.ToBase64String(Pdf("redacted")) };
        (await SendAsync(Client, HttpMethod.Post, Base + "/redact", redactBody, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var redacted = await SendAsync(Client, HttpMethod.Post, Base + "/redact", redactBody, Manager);
        redacted.Response.StatusCode.ShouldBe(HttpStatusCode.OK, redacted.Body?.ToJsonString());
        var doc = redacted.Body!["document"]!;
        doc["legalHold"]!.GetValue<bool>().ShouldBeTrue();
        doc["redacted"]!.GetValue<bool>().ShouldBeTrue();
        doc["archiveId"]!.GetValue<string>().ShouldBe(original);
        doc["redactedArchiveId"]!.GetValue<string>().ShouldNotBe(original);
        (await ScalarAsync($"SELECT count(*) FROM doc.archive WHERE archive_id IN ('{original}', '{doc["redactedArchiveId"]!.GetValue<string>()}')")).ShouldBe(2L);

        (await SendAsync(Client, HttpMethod.Post, Base + "/export", new { objectRef = obj }, Clerk)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var export = await SendAsync(Client, HttpMethod.Post, Base + "/export", new { objectRef = obj }, Manager);
        export.Response.StatusCode.ShouldBe(HttpStatusCode.OK, export.Body?.ToJsonString());
        export.Body!["document"]!["rows"]!.GetValue<int>().ShouldBe(1);
        export.Body["document"]!["content"]!.GetValue<string>().ShouldContain("id-card.pdf");
    }

    private static byte[] Pdf(string text) => Encoding.ASCII.GetBytes("%PDF-1.4\n" + text);

    private async Task<long> ScalarAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        return Convert.ToInt64(await dataSource.CreateCommand(sql).ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> SubmitAsync(HttpClient client, (string Name, byte[] Bytes)[] files, string? link = null) =>
        SendAsync(client, HttpMethod.Post, Base + "/submit", new
        {
            channel = "STAFF_UPLOAD",
            sender = new { name = "Test sender" },
            suggestedClass = "OTHER",
            link,
            files = files.Select(f => new { fileName = f.Name, content = Convert.ToBase64String(f.Bytes) }).ToArray(),
        }, Clerk);

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpClient client, HttpMethod method, string path, object? body, string roles)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, roles == Manager ? "mia" : "ann");
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private sealed class SwitchScanner(ScanVerdict verdict) : IMalwareScanner
    {
        public ScanVerdict Verdict { get; set; } = verdict;

        public Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken cancellationToken) => Task.FromResult(new ScanOutcome(Verdict, "switch"));
    }
}
