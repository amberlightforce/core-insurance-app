using System.Net;
using System.Text.Json.Nodes;
using CoreIns.CountryPacks.GR.Fiscal;
using CoreIns.Modules.Market.Contracts.Spi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Compliance;

/// <summary>
/// BO-CMP-06: resubmit, bulk resubmit, cancel, correct and qrPayload on the myDATA stub channel. A fiscal document is
/// never edited: rejected documents are sent again as they are; registered ones are credited, replaced or cancelled by
/// new linked documents. The channel is wrapped so a test can make it reject (the stub itself always registers).
/// </summary>
public sealed class FiscalOperationsTests : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private const string Admin = "Platform.Admin";
    private const string Base = "/api/cmp/v1/fiscal-documents";
    private readonly ApiHostFactory _factory;
    private readonly ToggleChannel _channel = new();

    public FiscalOperationsTests(PostgresFixture database)
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IFiscalDocumentChannel>(_channel))).CreateClient();

    private static object Request(string sourceId, Guid party, string amount = "100.00") => new
    {
        sourceType = "INVOICE",
        sourceId,
        role = "ISSUE",
        lines = new[] { new { fiscalCategoryKey = "PREMIUM-NONLIFE", amount = new { amount, currency = "EUR" } } },
        counterpartyPartyId = party,
        issueDate = "2026-10-07",
    };

    private async Task<string> IssueAsync(HttpClient client, bool rejected = false, string amount = "100.00")
    {
        _channel.Reject = rejected;
        var (response, body) = await SendAsync(client, HttpMethod.Post, $"{Base}/request", Request(Guid.CreateVersion7().ToString(), Guid.CreateVersion7(), amount), roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe(rejected ? "REJECTED" : "REGISTERED");
        _channel.Reject = false;
        return body.Text("fiscalDocumentId");
    }

    private static decimal Amount(JsonNode? document) =>
        decimal.Parse(document.Text("document.total.amount"), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<JsonNode?> GetAsync(HttpClient client, string id) =>
        (await SendAsync(client, HttpMethod.Get, $"{Base}/{id}", roles: Admin)).Body;

    [Fact]
    public async Task REQ_CMP_049_a_rejected_document_is_resubmitted_unchanged_and_a_replay_is_a_no_op()
    {
        using var client = Client();
        var id = await IssueAsync(client, rejected: true);
        var before = await GetAsync(client, id);
        before.Text("document.status").ShouldBe("REJECTED");

        var (response, body) = await SendAsync(client, HttpMethod.Post, $"{Base}/resubmit", new { fiscalDocumentId = id, reason = "Channel was down" }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("REGISTERED");
        body.Text("resubmitted").ShouldBe("true");

        var after = await GetAsync(client, id);
        after.Text("identifiers.number").ShouldBe(before.Text("identifiers.number")); // same document, same CMP number
        after.Text("identifiers.mark").ShouldStartWith("STUB-");

        var (_, replay) = await SendAsync(client, HttpMethod.Post, $"{Base}/resubmit", new { fiscalDocumentId = id, reason = "Again" }, roles: Admin);
        replay.Text("status").ShouldBe("REGISTERED");
        replay.Text("resubmitted").ShouldBe("false");
    }

    [Fact]
    public async Task A_resubmit_that_is_rejected_again_stays_rejected_and_the_input_is_checked()
    {
        using var client = Client();
        var id = await IssueAsync(client, rejected: true);
        _channel.Reject = true;
        var (_, body) = await SendAsync(client, HttpMethod.Post, $"{Base}/resubmit", new { fiscalDocumentId = id, reason = "Retry" }, roles: Admin);
        _channel.Reject = false;
        body.Text("status").ShouldBe("REJECTED");
        body.Text("rejectionCodes.0").ShouldBe("TEST_REJECT");

        (await SendAsync(client, HttpMethod.Post, $"{Base}/resubmit", new { fiscalDocumentId = Guid.CreateVersion7(), reason = "x" }, roles: Admin))
            .Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Post, $"{Base}/resubmit", new { fiscalDocumentId = id, reason = "" }, roles: Admin))
            .Response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(client, HttpMethod.Post, $"{Base}/resubmit", new { fiscalDocumentId = id, reason = "x" }, roles: Billing))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task REQ_CMP_050_a_bulk_resubmit_reports_each_document_and_is_capped()
    {
        using var client = Client();
        var rejected1 = await IssueAsync(client, rejected: true);
        var rejected2 = await IssueAsync(client, rejected: true);
        var registered = await IssueAsync(client);
        var unknown = Guid.CreateVersion7().ToString();

        var (response, body) = await SendAsync(client, HttpMethod.Post, $"{Base}/bulk-resubmit",
            new { ids = new[] { rejected1, rejected2, registered, unknown }, reason = "Batch retry" }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("requested").ShouldBe("4");
        body.Text("resubmitted").ShouldBe("2");
        body.Text("skipped").ShouldBe("2");
        var results = body!["results"]!.AsArray();
        results.Single(r => r!["fiscalDocumentId"]!.GetValue<string>() == rejected1)!["status"]!.GetValue<string>().ShouldBe("REGISTERED");
        results.Single(r => r!["fiscalDocumentId"]!.GetValue<string>() == unknown)!["errorCode"]!.GetValue<string>().ShouldBe("CMP-ERR-NOT-FOUND");
        (await GetAsync(client, rejected2)).Text("document.status").ShouldBe("REGISTERED");

        var tooMany = Enumerable.Range(0, 51).Select(_ => Guid.CreateVersion7()).ToArray();
        var (capped, error) = await SendAsync(client, HttpMethod.Post, $"{Base}/bulk-resubmit", new { ids = tooMany, reason = "x" }, roles: Admin);
        capped.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        error.Text("code").ShouldBe("CMP-ERR-BULK-LIMIT");
    }

    [Fact]
    public async Task REQ_CMP_051_correct_credits_the_original_and_issues_a_linked_replacement_once()
    {
        using var client = Client();
        var id = await IssueAsync(client, amount: "100.00");
        var correction = new { fiscalDocumentId = id, reason = "Wrong amount", corrections = new[] { new { fiscalCategoryKey = "PREMIUM-NONLIFE", amount = new { amount = "80.00", currency = "EUR" } } } };

        var (response, body) = await SendAsync(client, HttpMethod.Post, $"{Base}/correct", correction, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("creditStatus").ShouldBe("REGISTERED");
        body.Text("correctedStatus").ShouldBe("REGISTERED");

        var credit = await GetAsync(client, body.Text("creditFiscalDocumentId"));
        credit.Text("document.role").ShouldBe("CREDIT");
        credit.Text("document.correlatedDocumentId").ShouldBe(id);
        Amount(credit).ShouldBe(100m);
        credit.Text("identifiers.series").ShouldBe("STUB-A-CR");
        var replacement = await GetAsync(client, body.Text("correctedFiscalDocumentId"));
        replacement.Text("document.role").ShouldBe("ISSUE");
        replacement.Text("document.revision").ShouldBe("1");
        replacement.Text("document.correlatedDocumentId").ShouldBe(id);
        Amount(replacement).ShouldBe(80m);
        (await GetAsync(client, id)).Text("document.status").ShouldBe("REGISTERED"); // the original is never edited

        var (_, replay) = await SendAsync(client, HttpMethod.Post, $"{Base}/correct", correction, roles: Admin);
        replay.Text("creditFiscalDocumentId").ShouldBe(body.Text("creditFiscalDocumentId"));
        replay.Text("correctedFiscalDocumentId").ShouldBe(body.Text("correctedFiscalDocumentId"));

        var (_, cancel) = await SendAsync(client, HttpMethod.Post, $"{Base}/cancel", new { fiscalDocumentId = id, reason = "Too late" }, roles: Admin);
        cancel.Text("code").ShouldBe("CMP-ERR-NOT-CANCELLABLE");
    }

    [Fact]
    public async Task A_correction_whose_credit_is_rejected_stores_nothing_and_a_rejected_document_cannot_be_corrected()
    {
        using var client = Client();
        var id = await IssueAsync(client);
        var body = new { fiscalDocumentId = id, reason = "Fix", corrections = new[] { new { fiscalCategoryKey = "PREMIUM-NONLIFE", amount = new { amount = "70.00", currency = "EUR" } } } };

        _channel.Reject = true;
        var (response, error) = await SendAsync(client, HttpMethod.Post, $"{Base}/correct", body, roles: Admin);
        _channel.Reject = false;
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        error.Text("code").ShouldBe("CMP-ERR-NOT-CORRECTABLE");
        var (_, retry) = await SendAsync(client, HttpMethod.Post, $"{Base}/correct", body, roles: Admin);
        retry.Text("creditStatus").ShouldBe("REGISTERED"); // nothing was left behind by the failed attempt

        var rejected = await IssueAsync(client, rejected: true);
        (await SendAsync(client, HttpMethod.Post, $"{Base}/correct", new { fiscalDocumentId = rejected, reason = "Fix", corrections = body.corrections }, roles: Admin))
            .Body.Text("code").ShouldBe("CMP-ERR-NOT-CORRECTABLE");
        var another = await IssueAsync(client);
        (await SendAsync(client, HttpMethod.Post, $"{Base}/correct",
            new { fiscalDocumentId = another, reason = "Fix", corrections = new[] { new { fiscalCategoryKey = "PREMIUM-NONLIFE", amount = new { amount = "1.005", currency = "EUR" } } } }, roles: Admin))
            .Body.Text("code").ShouldBe("CMP-ERR-FISCAL-TOTAL");
    }

    [Fact]
    public async Task REQ_CMP_052_cancel_issues_a_linked_cancellation_for_a_registered_document_and_withdraws_a_rejected_one()
    {
        using var client = Client();
        var registered = await IssueAsync(client);
        var (response, body) = await SendAsync(client, HttpMethod.Post, $"{Base}/cancel", new { fiscalDocumentId = registered, reason = "Issued in error" }, roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("CANCELLED");
        var cancellation = await GetAsync(client, body.Text("cancellationFiscalDocumentId"));
        cancellation.Text("document.role").ShouldBe("CANCELLATION");
        cancellation.Text("document.status").ShouldBe("REGISTERED");
        cancellation.Text("document.correlatedDocumentId").ShouldBe(registered);
        cancellation.Text("identifiers.series").ShouldBe("STUB-A-CN");
        (await GetAsync(client, registered)).Text("document.status").ShouldBe("CANCELLED");

        var (_, replay) = await SendAsync(client, HttpMethod.Post, $"{Base}/cancel", new { fiscalDocumentId = registered, reason = "Again" }, roles: Admin);
        replay.Text("cancellationFiscalDocumentId").ShouldBe(body.Text("cancellationFiscalDocumentId"));
        (await SendAsync(client, HttpMethod.Post, $"{Base}/resubmit", new { fiscalDocumentId = registered, reason = "x" }, roles: Admin))
            .Body.Text("code").ShouldBe("CMP-ERR-NOT-REJECTED");

        var rejected = await IssueAsync(client, rejected: true);
        var (_, withdrawn) = await SendAsync(client, HttpMethod.Post, $"{Base}/cancel", new { fiscalDocumentId = rejected, reason = "Source withdrawn" }, roles: Admin);
        withdrawn.Text("status").ShouldBe("CANCELLED");
        withdrawn.Text("cancellationFiscalDocumentId").ShouldBe("null");
    }

    [Fact]
    public async Task REQ_CMP_054_qr_payload_is_served_for_a_registered_document_with_an_unverified_layout()
    {
        using var client = Client();
        var registered = await IssueAsync(client);
        var (response, body) = await SendAsync(client, HttpMethod.Get, $"{Base}/qr-payload?id={registered}", roles: Admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("REGISTERED");
        body.Text("qrPayload").ShouldContain(body.Text("mark"));
        body.Text("stub").ShouldBe("true");
        body.Text("layoutVerified").ShouldBe("false"); // open user decision #15

        var rejected = await IssueAsync(client, rejected: true);
        (await SendAsync(client, HttpMethod.Get, $"{Base}/qr-payload?id={rejected}", roles: Admin)).Body.Text("qrPayload").ShouldBe("null");
        (await SendAsync(client, HttpMethod.Get, $"{Base}/qr-payload?id={Guid.CreateVersion7()}", roles: Admin)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>The myDATA stub, except that it can be told to reject every submission.</summary>
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
