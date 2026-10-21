using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>SL4-CMP-RECEIPT: stub settlement receipt for CLM_CLAIM_PAYMENT (D-SL4-11, REQ-CMP-030, BR-CMP-004).</summary>
public sealed class ClaimReceiptTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Url = "/api/cmp/v1/fiscal-documents/request";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static object Body(string sourceId, Guid party, string amount = "800.00", string role = "ISSUE", string line = "CLAIM-SETTLEMENT", int revision = 0) => new
    {
        sourceType = "CLM_CLAIM_PAYMENT",
        sourceId,
        role,
        revision,
        documentCategory = "CLAIM_SETTLEMENT_RECEIPT",
        lines = new[] { new { fiscalCategoryKey = line, amount = new { amount, currency = "EUR" } } },
        counterpartyPartyId = party,
        issueDate = "2026-10-19",
    };

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(HttpClient client, object body) =>
        SendAsync(client, HttpMethod.Post, Url, body, roles: Admin);

    [Fact]
    public async Task One_receipt_per_payment_with_placeholder_codes_and_a_stub_mark_and_replay_returns_the_same()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();
        var payment = Guid.CreateVersion7().ToString();

        var (response, first) = await PostAsync(client, Body(payment, party));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, first?.ToJsonString());
        first.Text("status").ShouldBe("REGISTERED");
        first.Text("documentType").ShouldBe("UNMAPPED-OQ-012");

        var (_, replay) = await PostAsync(client, Body(payment, party));
        replay.Text("fiscalDocumentId").ShouldBe(first.Text("fiscalDocumentId"));

        var (_, got) = await SendAsync(client, HttpMethod.Get, $"/api/cmp/v1/fiscal-documents/{first.Text("fiscalDocumentId")}", roles: Admin);
        got.Text("document.sourceType").ShouldBe("CLM_CLAIM_PAYMENT");
        got.Text("document.documentTypeIsPlaceholder").ShouldBe("true");
        got.Text("identifiers.mark").ShouldStartWith("STUB-");
        got.Text("identifiers.stub").ShouldBe("true");
    }

    [Fact]
    public async Task The_registered_event_carries_no_personal_data()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();
        var (_, created) = await PostAsync(client, Body(Guid.CreateVersion7().ToString(), party));

        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var command = source.CreateCommand("SELECT payload::text FROM plt.outbox_message WHERE aggregate_id = @id AND event_type = 'FiscalDocRegistered'");
        command.Parameters.AddWithValue("id", created.Text("fiscalDocumentId"));
        var payload = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        payload.ShouldNotBeNull();
        payload.ShouldContain("STUB-");
        payload.ShouldNotContain(party.ToString());
        payload.ShouldNotContain("800");
    }

    [Fact]
    public async Task The_same_key_with_a_different_payload_is_a_409_mismatch()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();
        var payment = Guid.CreateVersion7().ToString();
        (await PostAsync(client, Body(payment, party))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await PostAsync(client, Body(payment, party, amount: "900.00"));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        body.Text("code").ShouldBe("CMP-ERR-IDEMPOTENCY-MISMATCH");
    }

    [Fact]
    public async Task The_same_key_and_total_with_different_line_detail_is_a_409_mismatch()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();
        var payment = Guid.CreateVersion7().ToString();
        (await PostAsync(client, Body(payment, party))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var split = new
        {
            sourceType = "CLM_CLAIM_PAYMENT",
            sourceId = payment,
            role = "ISSUE",
            revision = 0,
            documentCategory = "CLAIM_SETTLEMENT_RECEIPT",
            lines = new[]
            {
                new { fiscalCategoryKey = "CLAIM-SETTLEMENT", amount = new { amount = "500.00", currency = "EUR" } },
                new { fiscalCategoryKey = "CLAIM-SETTLEMENT", amount = new { amount = "300.00", currency = "EUR" } },
            },
            counterpartyPartyId = party,
            issueDate = "2026-10-19",
        };
        var (response, body) = await PostAsync(client, split);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body?.ToJsonString());
        body.Text("code").ShouldBe("CMP-ERR-IDEMPOTENCY-MISMATCH");

        // The identical payload still replays.
        (await PostAsync(client, Body(payment, party))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Clearing_lines_credits_and_cancellations_are_refused()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();

        var (clearing, clearingBody) = await PostAsync(client, Body(Guid.CreateVersion7().ToString(), party, line: "CLEARING"));
        clearing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        clearingBody.Text("code").ShouldBe("CMP-ERR-VALIDATION");

        foreach (var role in new[] { "CREDIT", "CANCELLATION" })
        {
            var (response, body) = await PostAsync(client, Body(Guid.CreateVersion7().ToString(), party, role: role));
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            body.Text("code").ShouldBe("CMP-ERR-VALIDATION");
        }
    }

    [Fact]
    public async Task Cyprus_resolves_not_required_and_stores_nothing()
    {
        await using var cy = new ApiHostFactory(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Stamp:Country"] = "CY" });
        using var client = cy.CreateClient();
        var payment = Guid.CreateVersion7().ToString();

        var (response, body) = await PostAsync(client, Body(payment, Guid.CreateVersion7()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("NOT_REQUIRED");

        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var command = source.CreateCommand("SELECT count(*) FROM cmp.fiscal_document WHERE source_id = @id");
        command.Parameters.AddWithValue("id", payment);
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(0L);
    }
}
