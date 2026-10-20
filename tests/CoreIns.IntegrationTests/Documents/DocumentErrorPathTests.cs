using System.Net;
using System.Text;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Documents;

/// <summary>NX-TST-07: document / archive / provider-event error paths and authorisation (complements <see cref="DocumentCoreTests"/>).</summary>
public sealed class DocumentErrorPathTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static object Request(string template = "DT-WITHDRAWAL-ACK", string? language = null, string? channel = null, bool requestNo = true) => new
    {
        documentType = template,
        subject = new { module = "BIL", type = "Case", id = Guid.NewGuid().ToString() },
        recipients = new[] { new { role = "ADDRESSEE", partyId = Guid.CreateVersion7() } },
        deliveryInstruction = new { mode = "EXPLICIT", channel },
        languageOverride = language,
        payloadSupplement = new
        {
            requestNo = requestNo ? Guid.NewGuid().ToString() : null,
            placeholders = new { customerName = "Μαρία", policyNo = "POL1", withdrawalDate = "2026-10-02", effectiveDate = "2026-11-02", refundAmount = "55.00 EUR" },
        },
    };

    [Fact]
    public async Task Document_operations_need_their_permissions()
    {
        using var client = _factory.CreateClient();
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", Request(), roles: "Viewer")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/{Guid.NewGuid()}", roles: "Viewer")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/store", new { binaryStream = "AAAA" }, roles: "Viewer")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/provider-events/stub-post", new { providerEventId = "e", deliveryId = Guid.NewGuid(), eventType = "DELIVERED" }, roles: "Viewer"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unapproved_language_an_unavailable_channel_and_a_missing_request_number_are_refused()
    {
        using var client = _factory.CreateClient();
        var (language, languageBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", Request(language: "fr"), roles: Admin);
        language.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, languageBody?.ToJsonString());
        languageBody.Text("code").ShouldBe("DOC-ERR-LANGUAGE-NOT-APPROVED");

        var (channel, channelBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", Request(channel: "CARRIER-PIGEON"), roles: Admin);
        channel.StatusCode.ShouldBe(HttpStatusCode.Forbidden, channelBody?.ToJsonString());
        channelBody.Text("code").ShouldBe("DOC-ERR-CHANNEL-NOT-PERMITTED");

        var (noNumber, noNumberBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", Request(requestNo: false), roles: Admin);
        noNumber.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noNumberBody.Text("code").ShouldBe("DOC-ERR-PAYLOAD-INVALID");
    }

    [Fact]
    public async Task Unknown_and_malformed_ids_are_not_found()
    {
        using var client = _factory.CreateClient();
        foreach (var path in new[]
                 {
                     $"/api/doc/v1/documents/{Guid.CreateVersion7()}", "/api/doc/v1/documents/not-a-guid", $"/api/doc/v1/archives/{Guid.CreateVersion7()}",
                     $"/api/doc/v1/evidence-packs/{Guid.CreateVersion7()}", $"/api/doc/v1/deliveries/status?documentId={Guid.CreateVersion7()}",
                 })
        {
            var (response, body) = await SendAsync(client, HttpMethod.Get, path, roles: Admin);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            body.Text("code").ShouldBe("DOC-ERR-NOT-FOUND");
        }

        var (badRef, badRefBody) = await SendAsync(client, HttpMethod.Get, "/api/doc/v1/documents/list-for-object?objectRef=nonsense", roles: Admin);
        badRef.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        badRefBody.Text("code").ShouldBe("DOC-ERR-PAYLOAD-INVALID");
    }

    [Fact]
    public async Task Provider_callbacks_for_an_unknown_provider_or_delivery_are_refused()
    {
        using var client = _factory.CreateClient();
        var body = new { providerEventId = Guid.NewGuid().ToString(), deliveryId = Guid.CreateVersion7(), eventType = "DELIVERED", proofDate = "2026-10-01", proofReference = "R" };
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/provider-events/no-such-provider", body, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/provider-events/stub-post", body, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Archive_store_checks_the_encoding_and_the_declared_hash_and_round_trips_valid_content()
    {
        using var client = _factory.CreateClient();
        var good = Convert.ToBase64String(Encoding.UTF8.GetBytes("archive me"));

        var (stored, storedBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/store", new { binaryStream = good }, roles: Admin);
        stored.StatusCode.ShouldBe(HttpStatusCode.OK, storedBody?.ToJsonString());
        var (read, readBody) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/archives/{storedBody.Text("archiveId")}", roles: Admin);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        readBody.Text("hash").ShouldBe(storedBody.Text("hash"));

        var (badEncoding, badEncodingBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/store", new { binaryStream = "***not base64***" }, roles: Admin);
        badEncoding.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        badEncodingBody.Text("code").ShouldBe("DOC-ERR-PAYLOAD-INVALID");

        var (mismatch, mismatchBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/archives/store", new { binaryStream = good, declaredHash = new string('0', 64) }, roles: Admin);
        mismatch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, mismatchBody?.ToJsonString());
        mismatchBody.Text("code").ShouldBe("DOC-ERR-HASH-MISMATCH");
    }

    [Fact]
    public async Task Reprint_needs_an_existing_document()
    {
        using var client = _factory.CreateClient();
        var (noId, noIdBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/reprint", new { }, roles: Admin);
        noId.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
        noIdBody.Text("code").ShouldStartWith("DOC-ERR-");

        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/reprint", new { documentId = Guid.CreateVersion7() }, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
