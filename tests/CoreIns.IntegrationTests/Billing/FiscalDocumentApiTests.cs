using System.Net;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>cmp.FiscalDocument.request / get on the myDATA stub channel (W5-CMP-01 subset built by SL-BIL).</summary>
public sealed class FiscalDocumentApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private readonly ApiHostFactory _factory = new(database.AppConnectionString);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static object Request(string sourceId, string sourceType = "TRANSACTION", string amount = "100.00") => new
    {
        sourceType,
        sourceId,
        role = "ISSUE",
        lines = new[] { new { fiscalCategoryKey = "PREMIUM-NONLIFE", amount = new { amount, currency = "EUR" } } },
        counterpartyPartyId = Guid.CreateVersion7(),
        issueDate = "2026-10-07",
    };

    [Fact]
    public async Task REQ_CMP_030_031_038_a_request_registers_once_per_fiscal_key_with_a_CMP_number_and_stub_identifiers()
    {
        using var client = _factory.CreateClient();
        var sourceId = Guid.CreateVersion7().ToString();
        var (first, one) = await SendAsync(client, HttpMethod.Post, "/api/cmp/v1/fiscal-documents/request", Request(sourceId), roles: Admin);
        var (second, two) = await SendAsync(client, HttpMethod.Post, "/api/cmp/v1/fiscal-documents/request", Request(sourceId), roles: Admin);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        second.StatusCode.ShouldBe(HttpStatusCode.OK, two?.ToJsonString());
        one.Text("status").ShouldBe("REGISTERED");
        two.Text("fiscalDocumentId").ShouldBe(one.Text("fiscalDocumentId")); // REQ-CMP-031: same source, role, revision

        var (got, document) = await SendAsync(client, HttpMethod.Get, $"/api/cmp/v1/fiscal-documents/{one.Text("fiscalDocumentId")}", roles: Admin);
        got.StatusCode.ShouldBe(HttpStatusCode.OK, document?.ToJsonString());
        document.Text("identifiers.series").ShouldBe("STUB-A");
        long.Parse(document.Text("identifiers.number"), System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
        document.Text("identifiers.mark").ShouldStartWith("STUB-");
        document.Text("identifiers.stub").ShouldBe("true");
        document.Text("document.documentType").ShouldBe("UNMAPPED-OQ-012");

        (await SendAsync(client, HttpMethod.Post, "/api/cmp/v1/fiscal-documents/request", Request(Guid.CreateVersion7().ToString(), "SOMETHING"), roles: Admin))
            .Body.Text("code").ShouldBe("CMP-ERR-SOURCE-UNKNOWN");
        (await SendAsync(client, HttpMethod.Post, "/api/cmp/v1/fiscal-documents/request", Request(Guid.CreateVersion7().ToString(), amount: "1.005"), roles: Admin))
            .Body.Text("code").ShouldBe("CMP-ERR-FISCAL-TOTAL");
        (await SendAsync(client, HttpMethod.Get, $"/api/cmp/v1/fiscal-documents/{Guid.CreateVersion7()}", roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Post, "/api/cmp/v1/fiscal-documents/request", Request(sourceId), roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
