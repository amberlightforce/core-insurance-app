using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Documents;

/// <summary>SL6-DOC-CORE: letters from templates, stub delivery, statutory delivery proof (D-SL6-03, REQ-DOC RT-11).</summary>
public sealed class DocumentCoreTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static object RequestBody(string template, string requestNo, Guid party, string subjectId, string? channel = null) => new
    {
        documentType = template,
        subject = new { module = "BIL", type = "DelinquencyCase", id = subjectId },
        recipients = new[] { new { role = "ADDRESSEE", partyId = party } },
        deliveryInstruction = new { mode = "EXPLICIT", channel },
        payloadSupplement = new
        {
            requestNo,
            placeholders = new
            {
                customerName = "Μαρία <Παπαδοπούλου>",
                policyNo = "POL000000123",
                amountDue = "120.00 EUR",
                dueDate = "2026-10-01",
                withdrawalDate = "2026-10-02",
                effectiveDate = "2026-11-02",
                refundAmount = "55.00 EUR",
            },
        },
    };

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> RequestAsync(HttpClient client, object body) =>
        await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", body, roles: Admin);

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> CallbackAsync(
        HttpClient client, string provider, Guid deliveryId, string type, string? proofDate = null, string? eventId = null) =>
        await SendAsync(client, HttpMethod.Post, $"/api/doc/v1/provider-events/{provider}",
            new { providerEventId = eventId ?? Guid.NewGuid().ToString(), deliveryId, eventType = type, proofDate, proofReference = "REG-1" }, roles: Admin);

    private static async Task<(string Id, Guid DeliveryId)> DeliveryOfAsync(HttpClient client, string documentId)
    {
        var (_, got) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/{documentId}", roles: Admin);
        return (documentId, Guid.Parse(got.Text("deliveries.0.deliveryId")));
    }

    private async Task<List<(string Type, JsonNode? Payload)>> EventsAsync(string documentId)
    {
        await using var source = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = source.CreateCommand("SELECT event_type, payload::text FROM plt.outbox_message WHERE aggregate_id = @id ORDER BY aggregate_sequence");
        command.Parameters.AddWithValue("id", documentId);
        var result = new List<(string, JsonNode?)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            result.Add((reader.GetString(0), JsonNode.Parse(reader.GetString(1))));
        }

        return result;
    }

    [Fact]
    public async Task A_nonpayment_notice_is_rendered_bilingual_html_and_goes_by_registered_post_only()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();
        var (response, body) = await RequestAsync(client, RequestBody("DT-NONPAY-NOTICE", Guid.NewGuid().ToString(), party, Guid.NewGuid().ToString(), channel: "EMAIL"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("Rendered");
        var documentId = body.Text("documentId");

        var (_, got) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/{documentId}", roles: Admin);
        got.Text("medium").ShouldBe("REGISTERED_POST"); // D-SL6-03: the requested e-mail is overruled
        got.Text("deliveries.0.channel").ShouldBe("stub-post");
        got.Text("deliveries.0.status").ShouldBe("Sent"); // rendering is not proof of delivery
        got.Text("deliveries.0.notificationDate").ShouldBe("null");

        var (archive, archiveBody) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/archives/{got.Text("archiveId")}", roles: Admin);
        archive.StatusCode.ShouldBe(HttpStatusCode.OK);
        archiveBody.Text("hash").ShouldBe(got.Text("contentHash"));

        var types = (await EventsAsync(documentId)).Select(e => e.Type).ToList();
        types.ShouldContain("DocumentRequested");
        types.ShouldContain("DocumentRendered");
        types.ShouldNotContain("DocumentDelivered");
    }

    [Fact]
    public async Task A_registered_post_proof_publishes_DocumentDelivered_with_the_notification_date()
    {
        using var client = _factory.CreateClient();
        var (_, body) = await RequestAsync(client, RequestBody("DT-NONPAY-NOTICE", Guid.NewGuid().ToString(), Guid.CreateVersion7(), Guid.NewGuid().ToString()));
        var (documentId, deliveryId) = await DeliveryOfAsync(client, body.Text("documentId"));
        var proofDate = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");

        var (noProof, noProofBody) = await CallbackAsync(client, "stub-post", deliveryId, "DELIVERED");
        noProof.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        noProofBody.Text("outcome").ShouldBe("REJECTED_NO_PROOF");
        (await EventsAsync(documentId)).Select(e => e.Type).ShouldNotContain("DocumentDelivered"); // no proof, no notificationDate

        var eventId = Guid.NewGuid().ToString();
        var (delivered, deliveredBody) = await CallbackAsync(client, "stub-post", deliveryId, "DELIVERED", proofDate, eventId);
        delivered.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        deliveredBody.Text("outcome").ShouldBe("APPLIED");

        var replay = await CallbackAsync(client, "stub-post", deliveryId, "DELIVERED", proofDate, eventId);
        replay.Body.Text("outcome").ShouldBe("DUPLICATE");

        var events = await EventsAsync(documentId);
        events.Count(e => e.Type == "DocumentDelivered").ShouldBe(1);
        var payload = events.Single(e => e.Type == "DocumentDelivered").Payload;
        payload.Text("notificationDate").ShouldBe(proofDate);
        payload.Text("medium").ShouldBe("REGISTERED_POST");

        var (_, evidence) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/deliveries/evidence-for?documentId={documentId}", roles: Admin);
        evidence.Text("statuses.deliveries.0.status").ShouldBe("Delivered");
        evidence.Text("statuses.deliveries.0.notificationDate").ShouldBe(proofDate);
        evidence.Text("completeness.complete").ShouldBe("true");
    }

    [Fact]
    public async Task A_returned_letter_publishes_DeliveryFailed_and_never_a_notification_date()
    {
        using var client = _factory.CreateClient();
        var (_, body) = await RequestAsync(client, RequestBody("DT-NONPAY-NOTICE", Guid.NewGuid().ToString(), Guid.CreateVersion7(), Guid.NewGuid().ToString()));
        var (documentId, deliveryId) = await DeliveryOfAsync(client, body.Text("documentId"));

        var (returned, returnedBody) = await CallbackAsync(client, "stub-post", deliveryId, "RETURNED");
        returned.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        returnedBody.Text("deliveryStatus").ShouldBe("Failed");

        var events = await EventsAsync(documentId);
        events.Select(e => e.Type).ShouldContain("DeliveryFailed");
        events.Single(e => e.Type == "DeliveryFailed").Payload.Text("reason").ShouldBe("RETURNED_MAIL");

        // A late "delivered" cannot revive a failed delivery.
        var late = await CallbackAsync(client, "stub-post", deliveryId, "DELIVERED", DateTime.UtcNow.ToString("yyyy-MM-dd"));
        late.Body.Text("outcome").ShouldBe("IGNORED_TERMINAL_CONFLICT"); // kept as evidence, flagged for a human
        (await EventsAsync(documentId)).Select(e => e.Type).ShouldNotContain("DocumentDelivered");
    }

    [Fact]
    public async Task A_statutory_notice_reprint_needs_a_reason_and_other_documents_do_not()
    {
        using var client = _factory.CreateClient();
        var (_, notice) = await RequestAsync(client, RequestBody("DT-NONPAY-NOTICE", Guid.NewGuid().ToString(), Guid.CreateVersion7(), Guid.NewGuid().ToString()));
        var noticeId = notice.Text("documentId");

        var (refused, refusedBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/reprint", new { documentId = noticeId }, roles: Admin);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, refusedBody?.ToJsonString());
        refusedBody.Text("code").ShouldBe("DOC-ERR-PAYLOAD-INVALID");

        var (blank, _) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/reprint", new { documentId = noticeId, reason = "  " }, roles: Admin);
        blank.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var (ok, okBody) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/reprint", new { documentId = noticeId, reason = "LOST_IN_POST" }, roles: Admin);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, okBody?.ToJsonString());

        var (_, ack) = await RequestAsync(client, RequestBody("DT-WITHDRAWAL-ACK", Guid.NewGuid().ToString(), Guid.CreateVersion7(), Guid.NewGuid().ToString()));
        (await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/reprint", new { documentId = ack.Text("documentId") }, roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_duplicate_request_returns_the_same_document_and_one_delivery()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();
        var requestNo = Guid.NewGuid().ToString();
        var subject = Guid.NewGuid().ToString();
        var first = (await RequestAsync(client, RequestBody("DT-WITHDRAWAL-ACK", requestNo, party, subject))).Body;
        var second = (await RequestAsync(client, RequestBody("DT-WITHDRAWAL-ACK", requestNo, party, subject))).Body;
        second.Text("documentId").ShouldBe(first.Text("documentId"));
        second.Text("documentNumber").ShouldBe(first.Text("documentNumber"));

        var (_, got) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/{first.Text("documentId")}", roles: Admin);
        got.Text("medium").ShouldBe("EMAIL");
        got!["deliveries"]!.AsArray().Count.ShouldBe(1);
        (await EventsAsync(first.Text("documentId"))).Count(e => e.Type == "DocumentRendered").ShouldBe(1);

        var (_, list) = await SendAsync(client, HttpMethod.Get, $"/api/doc/v1/documents/list-for-object?objectRef=BIL/DelinquencyCase/{subject}", roles: Admin);
        list!["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_missing_placeholder_or_unknown_template_is_refused()
    {
        using var client = _factory.CreateClient();
        var body = new
        {
            documentType = "DT-REFUND-ADVICE",
            subject = new { module = "BIL", type = "Refund", id = "r-1" },
            recipients = new[] { new { role = "ADDRESSEE", partyId = Guid.CreateVersion7() } },
            deliveryInstruction = new { mode = "EXPLICIT" },
            payloadSupplement = new { requestNo = Guid.NewGuid().ToString(), placeholders = new { customerName = "A" } },
        };
        var (missing, missingBody) = await RequestAsync(client, body);
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missingBody.Text("code").ShouldBe("DOC-ERR-PAYLOAD-INVALID");

        var (unknown, _) = await RequestAsync(client, RequestBody("DT-NOPE", Guid.NewGuid().ToString(), Guid.CreateVersion7(), "x"));
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
