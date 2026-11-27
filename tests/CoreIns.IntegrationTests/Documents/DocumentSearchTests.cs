using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Documents;

/// <summary>BO-DOC-01: <c>doc.Document.search</c> filters, keyset paging, staff-only access and legal-entity isolation.</summary>
public sealed class DocumentSearchTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static object RequestBody(string template, string requestNo, Guid party, string subjectId) => new
    {
        documentType = template,
        subject = new { module = "BIL", type = "DelinquencyCase", id = subjectId },
        recipients = new[] { new { role = "ADDRESSEE", partyId = party } },
        deliveryInstruction = new { mode = "EXPLICIT" },
        payloadSupplement = new
        {
            requestNo,
            placeholders = new
            {
                customerName = "Μαρία", policyNo = "POL000000123", amountDue = "120.00 EUR", dueDate = "2026-10-01",
                withdrawalDate = "2026-10-02", effectiveDate = "2026-11-02", refundAmount = "55.00 EUR",
            },
        },
    };

    private static async Task<string> RequestAsync(HttpClient client, string template, string requestNo, Guid party, string subjectId)
    {
        var (response, body) = await SendAsync(client, HttpMethod.Post, "/api/doc/v1/documents/request", RequestBody(template, requestNo, party, subjectId), roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body.Text("documentId");
    }

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> SearchAsync(HttpClient client, string query, string roles = Admin) =>
        await SendAsync(client, HttpMethod.Get, "/api/doc/v1/documents/search" + query, roles: roles);

    private static List<string> Ids(JsonNode? body) => [.. body!["items"]!.AsArray().Select(i => i!["documentId"]!.GetValue<string>())];

    [Fact]
    public async Task Filters_narrow_by_type_subject_recipient_text_and_dates()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var party = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var nonpay = await RequestAsync(client, "DT-NONPAY-NOTICE", $"REQ-{marker}-A", party, $"case-{marker}-a");
        var refund = await RequestAsync(client, "DT-REFUND-ADVICE", $"REQ-{marker}-B", other, $"case-{marker}-b");

        var (_, byText) = await SearchAsync(client, $"?q=req-{marker}");
        Ids(byText).ShouldBe([refund, nonpay], ignoreOrder: true);

        var (_, byType) = await SearchAsync(client, $"?q=REQ-{marker}&templateKey=DT-REFUND-ADVICE");
        Ids(byType).ShouldBe([refund]);

        var (_, byRecipient) = await SearchAsync(client, $"?recipientPartyId={party}");
        Ids(byRecipient).ShouldBe([nonpay]);

        var (_, bySubject) = await SearchAsync(client, $"?subjectModule=BIL&subjectType=DelinquencyCase&subjectId=case-{marker}-b");
        Ids(bySubject).ShouldBe([refund]);

        var (_, byMedium) = await SearchAsync(client, $"?q=REQ-{marker}&medium=REGISTERED_POST");
        Ids(byMedium).ShouldBe([nonpay]);

        var (_, byStatus) = await SearchAsync(client, $"?q=REQ-{marker}&status=Failed");
        Ids(byStatus).ShouldBeEmpty();

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var (_, inRange) = await SearchAsync(client, $"?q=REQ-{marker}&createdFrom={today}&createdTo={today}");
        Ids(inRange).Count.ShouldBe(2);
        var (_, past) = await SearchAsync(client, $"?q=REQ-{marker}&createdTo=2020-01-01");
        Ids(past).ShouldBeEmpty();

        var item = byText!["items"]!.AsArray().Single(i => i!["documentId"]!.GetValue<string>() == nonpay);
        item!.Text("latestDeliveryStatus").ShouldBe("Sent");
        item.Text("deliveries.0.channel").ShouldBe("stub-post");
    }

    [Fact]
    public async Task Paging_is_keyset_stable_while_new_documents_arrive()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var party = Guid.CreateVersion7();
        var created = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            created.Add(await RequestAsync(client, "DT-REFUND-ADVICE", $"PG-{marker}-{i}", party, $"pg-{marker}-{i}"));
        }

        var (_, first) = await SearchAsync(client, $"?q=PG-{marker}&limit=2");
        Ids(first).Count.ShouldBe(2);
        var cursor = first!.Text("nextCursor");
        cursor.ShouldNotBe("null");

        await RequestAsync(client, "DT-REFUND-ADVICE", $"PG-{marker}-late", party, $"pg-{marker}-late");

        var seen = Ids(first);
        while (cursor != "null")
        {
            var (_, page) = await SearchAsync(client, $"?q=PG-{marker}&limit=2&cursor={cursor}");
            seen.AddRange(Ids(page));
            cursor = page!.Text("nextCursor");
        }

        seen.Count.ShouldBe(seen.Distinct().Count());
        seen.ShouldBe([.. created.AsEnumerable().Reverse()]); // newest first; the late arrival sorts before the cursor so it is not served
    }

    [Fact]
    public async Task A_malformed_cursor_a_bad_limit_or_a_bad_date_is_refused()
    {
        using var client = _factory.CreateClient();
        (await SearchAsync(client, "?cursor=%25%25")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SearchAsync(client, "?limit=0")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SearchAsync(client, "?limit=201")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SearchAsync(client, "?createdFrom=yesterday")).Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Search_is_staff_only()
    {
        using var client = _factory.CreateClient();
        (await SearchAsync(client, string.Empty, roles: Underwriter)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SearchAsync(client, string.Empty, roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Documents_of_another_legal_entity_are_never_returned()
    {
        using var client = _factory.CreateClient();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var mine = await RequestAsync(client, "DT-REFUND-ADVICE", $"LE-{marker}-mine", Guid.CreateVersion7(), $"le-{marker}");

        var foreign = Guid.CreateVersion7();
        await using (var source = NpgsqlDataSource.Create(database.SuperuserConnectionString))
        {
            await using var command = source.CreateCommand(
                "INSERT INTO doc.document (document_id, legal_entity_id, document_number, template_key, template_version, request_no, subject_module, subject_type, subject_id, "
                + "recipient_party_id, language, status, medium, archive_id, content_hash, created_at, created_by, rendered_at) "
                + "SELECT @id, gen_random_uuid(), document_number || '-X', template_key, template_version, @req, subject_module, subject_type, subject_id, "
                + "recipient_party_id, language, status, medium, archive_id, content_hash, created_at, created_by, rendered_at FROM doc.document WHERE document_id = @mine");
            command.Parameters.AddWithValue("id", foreign);
            command.Parameters.AddWithValue("req", $"LE-{marker}-foreign");
            command.Parameters.AddWithValue("mine", Guid.Parse(mine));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (_, body) = await SearchAsync(client, $"?q=LE-{marker}");
        Ids(body).ShouldBe([mine]);
    }
}
