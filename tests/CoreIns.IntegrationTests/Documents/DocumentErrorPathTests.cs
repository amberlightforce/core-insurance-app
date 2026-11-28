using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
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

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> AsAsync(HttpClient client, string user, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Admin);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static async Task<(string DocumentId, Guid DeliveryId)> NoticeAsync(HttpClient client)
    {
        var body = new
        {
            documentType = "DT-NONPAY-NOTICE",
            subject = new { module = "BIL", type = "DelinquencyCase", id = Guid.NewGuid().ToString() },
            recipients = new[] { new { role = "ADDRESSEE", partyId = Guid.CreateVersion7() } },
            deliveryInstruction = new { mode = "EXPLICIT" },
            payloadSupplement = new
            {
                requestNo = Guid.NewGuid().ToString(),
                placeholders = new { customerName = "Μαρία", policyNo = "POL1", amountDue = "120.00 EUR", dueDate = "2026-10-01" },
            },
        };
        var (_, created) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", body, roles: Admin);
        var documentId = created.Text("documentId");
        var (_, got) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/{documentId}", roles: Admin);
        return (documentId, Guid.Parse(got.Text("deliveries.0.deliveryId")));
    }

    private static async Task<int> DeliveryCountAsync(HttpClient client, string documentId) =>
        (await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/{documentId}", roles: Admin)).Body!["deliveries"]!.AsArray().Count;

    [Fact]
    public async Task A_statutory_reprint_waits_for_a_second_person_and_the_maker_cannot_approve_it()
    {
        using var client = _factory.CreateClient();
        var (documentId, _) = await NoticeAsync(client);

        var (pending, pendingBody) = await AsAsync(client, "maker", "/api/doc/v1/documents/reprint", new { documentId, reason = "LOST_IN_POST" });
        pending.StatusCode.ShouldBe(HttpStatusCode.OK, pendingBody?.ToJsonString());
        var link = pendingBody!["link"]!;
        link.Text("status").ShouldBe("PENDING_APPROVAL");
        var requestId = link.Text("approvalRequestId");
        (await DeliveryCountAsync(client, documentId)).ShouldBe(1); // no reprint yet

        var (self, selfBody) = await AsAsync(client, "maker", "/api/doc/v1/documents/decide-reprint", new { approvalRequestId = requestId, decision = "APPROVE" });
        self.StatusCode.ShouldNotBe(HttpStatusCode.OK, selfBody?.ToJsonString());
        selfBody.Text("code").ShouldContain("SELF-APPROVAL");
        (await DeliveryCountAsync(client, documentId)).ShouldBe(1);

        var (ok, okBody) = await AsAsync(client, "checker", "/api/doc/v1/documents/decide-reprint", new { approvalRequestId = requestId, decision = "APPROVE" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());
        okBody.Text("status").ShouldBe("APPROVED");
        (await DeliveryCountAsync(client, documentId)).ShouldBe(2);

        // A decided request cannot be decided again: no second delivery.
        var (again, _) = await AsAsync(client, "checker2", "/api/doc/v1/documents/decide-reprint", new { approvalRequestId = requestId, decision = "APPROVE" });
        again.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await DeliveryCountAsync(client, documentId)).ShouldBe(2);
    }

    [Fact]
    public async Task A_rejected_statutory_reprint_creates_no_delivery()
    {
        using var client = _factory.CreateClient();
        var (documentId, _) = await NoticeAsync(client);
        var (_, pendingBody) = await AsAsync(client, "maker", "/api/doc/v1/documents/reprint", new { documentId, reason = "CUSTOMER_REQUEST" });
        var requestId = pendingBody!["link"]!.Text("approvalRequestId");

        var (rejected, rejectedBody) = await AsAsync(client, "checker", "/api/doc/v1/documents/decide-reprint", new { approvalRequestId = requestId, decision = "REJECT", comment = "no" });
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, rejectedBody?.ToJsonString());
        rejectedBody.Text("status").ShouldBe("REJECTED");
        (await DeliveryCountAsync(client, documentId)).ShouldBe(1);
    }

    [Fact]
    public async Task Deciding_an_unknown_reprint_request_is_refused_and_needs_the_permission()
    {
        using var client = _factory.CreateClient();
        var (response, _) = await AsAsync(client, "checker", "/api/doc/v1/documents/decide-reprint", new { approvalRequestId = Guid.CreateVersion7(), decision = "APPROVE" });
        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/decide-reprint", new { approvalRequestId = Guid.CreateVersion7(), decision = "APPROVE" }, roles: "Viewer"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_conflicting_provider_callback_raises_exactly_one_open_delivery_conflict_activity()
    {
        using var client = _factory.CreateClient();
        var (documentId, deliveryId) = await NoticeAsync(client);

        async Task<string> CallbackAsync(string type, string? proof = null) =>
            (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/provider-events/stub-post",
                new { providerEventId = Guid.NewGuid().ToString(), deliveryId, eventType = type, proofDate = proof, proofReference = "REG-1" }, roles: Admin)).Body.Text("outcome");

        (await CallbackAsync("RETURNED")).ShouldBe("APPLIED");
        (await OpenActivitiesAsync(documentId)).ShouldBe(0); // no conflict yet

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        (await CallbackAsync("DELIVERED", today)).ShouldBe("IGNORED_TERMINAL_CONFLICT");
        (await CallbackAsync("DELIVERED", today)).ShouldBe("IGNORED_TERMINAL_CONFLICT");
        (await OpenActivitiesAsync(documentId)).ShouldBe(1);

        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var queue = source.CreateCommand($"SELECT queue_code FROM wrk.activity WHERE pattern_code = 'WRK-DOC-DELIVERY-CONFLICT' AND primary_id = '{documentId}'");
        (await queue.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("DOC-OPS");
    }

    private async Task<long> OpenActivitiesAsync(string documentId)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand($"SELECT count(*) FROM wrk.activity WHERE pattern_code = 'WRK-DOC-DELIVERY-CONFLICT' AND primary_id = '{documentId}' AND state = 'OPEN'");
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
