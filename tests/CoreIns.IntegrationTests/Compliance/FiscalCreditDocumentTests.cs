using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>SL3-CMP-CREDIT: credit documents through the stub channel, correlated to the original (REQ-CMP-032, BR-CMP-004).</summary>
public sealed class FiscalCreditDocumentTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Url = "/api/cmp/v1/fiscal-documents/request";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static object Body(string role, string sourceId, Guid party, int revision = 0, Guid? correlated = null) => new
    {
        sourceType = role == "CREDIT" ? "CREDIT" : "INVOICE",
        sourceId,
        role,
        revision,
        correlatedDocumentId = correlated,
        lines = new[] { new { fiscalCategoryKey = "PREMIUM-NONLIFE", amount = new { amount = "50.00", currency = "EUR" } } },
        counterpartyPartyId = party,
        issueDate = "2026-10-07",
    };

    private static async Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(HttpClient client, object body) =>
        await SendAsync(client, HttpMethod.Post, Url, body, roles: Admin);

    private static async Task<Guid> IssueAsync(HttpClient client, Guid party)
    {
        var (response, body) = await PostAsync(client, Body("ISSUE", Guid.CreateVersion7().ToString(), party));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("REGISTERED");
        return Guid.Parse(body.Text("fiscalDocumentId"));
    }

    [Fact]
    public async Task REQ_CMP_032_a_credit_for_a_registered_original_is_registered_with_a_placeholder_type_and_the_correlation()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();
        var original = await IssueAsync(client, party);

        var (response, credit) = await PostAsync(client, Body("CREDIT", Guid.CreateVersion7().ToString(), party, correlated: original));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, credit?.ToJsonString());
        credit.Text("status").ShouldBe("REGISTERED");
        credit.Text("documentType").ShouldBe("UNMAPPED-OQ-012");

        var (_, got) = await SendAsync(client, HttpMethod.Get, $"/api/cmp/v1/fiscal-documents/{credit.Text("fiscalDocumentId")}", roles: Admin);
        got.Text("document.role").ShouldBe("CREDIT");
        got.Text("document.correlatedDocumentId").ShouldBe(original.ToString());
        got.Text("document.documentTypeIsPlaceholder").ShouldBe("true");
        got.Text("identifiers.mark").ShouldStartWith("STUB-");
        got.Text("identifiers.series").ShouldBe("STUB-A-CR"); // CMP's own credit series, apart from the invoices
        got.Text("identifiers.stub").ShouldBe("true");
    }

    [Fact]
    public async Task BR_CMP_004_a_replay_returns_the_same_document_and_a_new_revision_a_new_one()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();
        var original = await IssueAsync(client, party);
        var source = Guid.CreateVersion7().ToString();

        var first = (await PostAsync(client, Body("CREDIT", source, party, 1, original))).Body;
        var replay = (await PostAsync(client, Body("CREDIT", source, party, 1, original))).Body;
        var next = (await PostAsync(client, Body("CREDIT", source, party, 2, original))).Body;

        replay.Text("fiscalDocumentId").ShouldBe(first.Text("fiscalDocumentId"));
        next.Text("fiscalDocumentId").ShouldNotBe(first.Text("fiscalDocumentId"));
        next.Text("status").ShouldBe("REGISTERED");
    }

    [Fact]
    public async Task REQ_CMP_032_a_credit_without_a_usable_original_is_refused()
    {
        using var client = _factory.CreateClient();
        var party = Guid.CreateVersion7();
        var original = await IssueAsync(client, party);
        var credit = Guid.Parse((await PostAsync(client, Body("CREDIT", Guid.CreateVersion7().ToString(), party, correlated: original))).Body.Text("fiscalDocumentId"));

        var (missing, missingBody) = await PostAsync(client, Body("CREDIT", Guid.CreateVersion7().ToString(), party, correlated: Guid.CreateVersion7()));
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missingBody.Text("code").ShouldBe("CMP-ERR-CORRELATED-NOT-FOUND");

        var (ofCredit, ofCreditBody) = await PostAsync(client, Body("CREDIT", Guid.CreateVersion7().ToString(), party, correlated: credit));
        ofCredit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        ofCreditBody.Text("code").ShouldBe("CMP-ERR-CORRELATED-NOT-FOUND");

        var (other, otherBody) = await PostAsync(client, Body("CREDIT", Guid.CreateVersion7().ToString(), Guid.CreateVersion7(), correlated: original));
        other.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        otherBody.Text("code").ShouldBe("CMP-ERR-CORRELATED-NOT-FOUND");

        var (issue, issueBody) = await PostAsync(client, Body("ISSUE", Guid.CreateVersion7().ToString(), party, correlated: original));
        issue.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        issueBody.Text("code").ShouldBe("CMP-ERR-VALIDATION");
    }
}
