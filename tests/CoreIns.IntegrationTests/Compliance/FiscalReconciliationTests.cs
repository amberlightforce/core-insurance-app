using System.Net;
using System.Text.Json.Nodes;
using CoreIns.CountryPacks.GR.Fiscal;
using CoreIns.Modules.Market.Contracts.Spi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>BO-CMP-07: fiscal reconciliation run (break rows, idempotent re-run, auto-resolve), breaks list and the certify rule.</summary>
public sealed class FiscalReconciliationTests : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Docs = "/api/cmp/v1/fiscal-documents";
    private const string Recon = "/api/cmp/v1/fiscal-reconciliation";
    private readonly ApiHostFactory _factory;
    private readonly ToggleChannel _channel = new();

    public FiscalReconciliationTests(PostgresFixture database)
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IFiscalDocumentChannel>(_channel))).CreateClient();

    private async Task<string> IssueAsync(HttpClient client, bool rejected)
    {
        _channel.Reject = rejected;
        var request = new
        {
            sourceType = "INVOICE",
            sourceId = Guid.CreateVersion7().ToString(),
            role = "ISSUE",
            lines = new[] { new { fiscalCategoryKey = "PREMIUM-NONLIFE", amount = new { amount = "100.00", currency = "EUR" } } },
            counterpartyPartyId = Guid.CreateVersion7(),
            issueDate = "2026-10-07",
        };
        var (response, body) = await SendAsync(client, HttpMethod.Post, $"{Docs}/request", request, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        _channel.Reject = false;
        return body.Text("fiscalDocumentId");
    }

    private static async Task<JsonNode?> RunAsync(HttpClient client, string query = "")
    {
        var (response, body) = await SendAsync(client, HttpMethod.Post, $"{Recon}/run{query}", new { }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return body;
    }

    private static async Task<List<JsonNode>> BreaksAsync(HttpClient client)
    {
        var (response, body) = await SendAsync(client, HttpMethod.Get, $"{Recon}/breaks?limit=200", roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        return [.. body!["items"]!.AsArray().Select(i => i!["breaks"]!)];
    }

    private static JsonNode? BreakOf(List<JsonNode> breaks, string id) =>
        breaks.SingleOrDefault(b => b["document"]!["fiscalDocumentId"]!.GetValue<string>() == id);

    [Fact]
    public async Task REQ_CMP_053_a_run_records_a_break_for_a_rejected_document_and_lists_it_with_the_document()
    {
        using var client = Client();
        var rejected = await IssueAsync(client, rejected: true);
        var registered = await IssueAsync(client, rejected: false);

        var run = await RunAsync(client);
        int.Parse(run.Text("checked")).ShouldBeGreaterThanOrEqualTo(2);
        run!["breaks"]!.AsArray().Any(b => b!["fiscalDocumentId"]!.GetValue<string>() == rejected && b["kind"]!.GetValue<string>() == "REJECTED_BY_CHANNEL").ShouldBeTrue();

        var breaks = await BreaksAsync(client);
        var found = BreakOf(breaks, rejected);
        found.ShouldNotBeNull();
        found!["kind"]!.GetValue<string>().ShouldBe("REJECTED_BY_CHANNEL");
        found["status"]!.GetValue<string>().ShouldBe("OPEN");
        found["document"]!["status"]!.GetValue<string>().ShouldBe("REJECTED");
        found["detail"]!.GetValue<string>().ShouldContain("TEST_REJECT");
        BreakOf(breaks, registered).ShouldBeNull();
    }

    [Fact]
    public async Task A_re_run_is_idempotent_and_a_fixed_document_resolves_its_break()
    {
        using var client = Client();
        var rejected = await IssueAsync(client, rejected: true);
        await RunAsync(client);
        var before = await BreaksAsync(client);

        var again = await RunAsync(client);
        again.Text("opened").ShouldBe("0");
        again.Text("resolved").ShouldBe("0");
        var after = await BreaksAsync(client);
        after.Count.ShouldBe(before.Count);
        BreakOf(after, rejected)!["breakId"]!.GetValue<string>().ShouldBe(BreakOf(before, rejected)!["breakId"]!.GetValue<string>());

        var (resubmit, _) = await SendAsync(client, HttpMethod.Post, $"{Docs}/resubmit", new { fiscalDocumentId = rejected, reason = "Channel back" }, roles: Admin);
        resubmit.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fixedRun = await RunAsync(client);
        int.Parse(fixedRun.Text("resolved")).ShouldBeGreaterThanOrEqualTo(1);
        BreakOf(await BreaksAsync(client), rejected).ShouldBeNull();
    }

    [Fact]
    public async Task The_run_needs_the_compliance_permission_and_a_dry_run_records_nothing()
    {
        using var client = Client();
        var rejected = await IssueAsync(client, rejected: true);
        (await SendAsync(client, HttpMethod.Post, $"{Recon}/run", new { }, roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Get, $"{Recon}/breaks", roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var dry = await RunAsync(client, "?dryRun=true");
        int.Parse(dry.Text("opened")).ShouldBeGreaterThanOrEqualTo(1);
        BreakOf(await BreaksAsync(client), rejected).ShouldBeNull();
    }

    [Fact]
    public async Task REQ_CMP_260_certify_fails_closed_unless_every_key_passes_and_the_values_are_verified()
    {
        using var client = Client();
        const string Url = "/api/cmp/v1/fiscal-rules/certify";
        static object Candidate(string? documentType = "MYDATA-X", string? pattern = "A-{0000}", bool issue = true, bool delivery = true, string? legal = "Verified") =>
            new { packCandidate = new { documentType, seriesPattern = pattern, markBeforeIssue = issue, markBeforeDelivery = delivery, legalStatus = legal } };

        var (ok, pass) = await SendAsync(client, HttpMethod.Post, Url, Candidate(), roles: Admin);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, pass?.ToJsonString());
        pass.Text("pass").ShouldBe("true");

        var (_, unverified) = await SendAsync(client, HttpMethod.Post, Url, Candidate(legal: null), roles: Admin);
        unverified.Text("pass").ShouldBe("false");
        unverified.Text("failedKeys.0").ShouldBe("cmp.fiscal.legal_status_verified");

        var (_, bad) = await SendAsync(client, HttpMethod.Post, Url, Candidate(documentType: "UNMAPPED-OQ-012", pattern: "", issue: false, delivery: false), roles: Admin);
        bad.Text("pass").ShouldBe("false");
        bad!["failedKeys"]!.AsArray().Count.ShouldBe(4);

        (await SendAsync(client, HttpMethod.Post, Url, Candidate(), roles: Billing)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private sealed class ToggleChannel : IFiscalDocumentChannel
    {
        private readonly MyDataStubFiscalChannel _stub = new();

        public volatile bool Reject;

        public Task<FiscalDocument> BuildAsync(FiscalSource fiscalSource, CancellationToken cancellationToken = default) => _stub.BuildAsync(fiscalSource, cancellationToken);

        public Task<FiscalSubmissionResult> SubmitAsync(FiscalDocument document, string idempotencyKey, CancellationToken cancellationToken = default) =>
            Reject
                ? Task.FromResult(new FiscalSubmissionResult(FiscalSubmissionStatus.Rejected, null, null, [new SpiError(SpiErrorCategory.Validation, "TEST_REJECT")]))
                : _stub.SubmitAsync(document, idempotencyKey, cancellationToken);

        public Task<FiscalSubmissionResult> CancelAsync(string registrationId, string reason, CancellationToken cancellationToken = default) =>
            _stub.CancelAsync(registrationId, reason, cancellationToken);

        public Task<FiscalSeriesRules> SeriesAsync(string documentType, CancellationToken cancellationToken = default) => _stub.SeriesAsync(documentType, cancellationToken);
    }
}
