using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Claims.Events;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Policy;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.FriendlySettlement;

/// <summary>
/// SL4-CLM-FS-STATEMENT main paths over the real host, database and the GR stub clearing adapter: import and match, FS recovery,
/// CLEARING payable issued, net per counterparty, approval, hand-over to BIL (scripted double) and the BIL outcome.
/// </summary>
public sealed class FsStatementTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string Insurer = Guid.NewGuid().ToString("D");
    private readonly Guid ClearingAccount = Guid.NewGuid();
    private readonly FakePolicyPolicyService _policies = new();
    private readonly FakeDisbursements _disbursements = new();
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(
            database.AppConnectionString,
            services =>
            {
                services.AddSingleton<IPolicyPolicyService>(_policies);
                services.AddSingleton<IBillingDisbursementService>(_disbursements);
            },
            new Dictionary<string, string?>
            {
                ["FriendlySettlement:Stub:OwnInsurerCode"] = "OWN-INSURER",
                ["FriendlySettlement:Stub:MemberCodes:0"] = "OWN-INSURER",
                ["FriendlySettlement:Stub:MemberCodes:1"] = Insurer,
                [$"Claims:FriendlySettlement:ClearingPayeeAccounts:{Insurer}"] = ClearingAccount.ToString("D"),
            });
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private async Task<MoneyClaim> ClaimAsync(ScriptedPolicy policy, decimal fault, double daysAgo)
    {
        var claim = await _money.OpenClaimAsync(ClaimsSlice.Fnol(policy, ClaimsSlice.DaysAgo(daysAgo)));
        await database.ExecuteAsSuperuserAsync(
            $"UPDATE clm.claim SET fault_insured_pct={fault.ToString(System.Globalization.CultureInfo.InvariantCulture)}, fault_source='JOINT_REPORT', "
            + $"counterparty_insurer_party_id='{Insurer}', joint_accident_report=true, vehicle_count=2, accident_in_greece=true WHERE claim_id='{claim.ClaimId}'", ClaimsSlice.Ct);
        return claim;
    }

    private async Task<JsonNode> PostAsync(string path, object body, HttpStatusCode expected = HttpStatusCode.OK, string roles = Manager, string user = ManagerUser)
    {
        var (response, json) = await _money.SendAsync(HttpMethod.Post, path, body, roles, user);
        response.StatusCode.ShouldBe(expected, json?.ToJsonString());
        return json!;
    }

    /// <summary>Own leg (1,200 receivable, submitted) and at-fault leg (3,000 CLEARING payment, approved) in the same month; returns the clearing references.</summary>
    private async Task<(string Own, string AtFault, string Month)> LegsAsync(double daysAgo, decimal ownValue = 1200m)
    {
        var own = await ClaimAsync(_slice.Policy(), fault: 0m, daysAgo);
        await _money.BuildAndSubmitAsync(own, [Reserve(own, ownValue)]);
        var evaluated = await PostAsync("/api/clm/v1/friendly-settlements/evaluate", new { claimId = own.ClaimId });
        var opened = await PostAsync("/api/clm/v1/friendly-settlements/open-own-settlement",
            new { fsCaseId = evaluated.Text("fsCase.fsCaseId"), expectedRecordVersion = evaluated["fsCase"]!["recordVersion"]!.GetValue<int>() });
        var submitted = await PostAsync("/api/clm/v1/friendly-settlements/submit",
            new { fsCaseId = evaluated.Text("fsCase.fsCaseId"), expectedRecordVersion = opened["fsCase"]!["recordVersion"]!.GetValue<int>() });

        var policy = _slice.Policy();
        var atFault = await ClaimAsync(policy, fault: 100m, daysAgo);
        var lossDate = await _money.ScalarAsync<string>($"SELECT loss_date::text FROM clm.claim WHERE claim_id='{atFault.ClaimId}'");
        var reference = "STUB-FS-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var received = await PostAsync("/api/clm/v1/dev/fs-clearing/notifications", new
        {
            clearingReference = reference, policyNumber = policy.PolicyNumber, lossDate, counterpartyInsurerPartyId = Insurer,
            amount = "3000.00", currency = "EUR", damageType = "MATERIAL_DAMAGE",
        });
        var set = await _money.SetAsync(received.Text("setId"));
        foreach (var approval in set["approvals"]?.AsArray() ?? [])
        {
            (await _money.DecideAsync(approval!.Text("approvalRequestId"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await _money.DrainAsync();
        var month = (await _money.ScalarAsync<string>($"SELECT to_char(loss_date, 'YYYY-MM') FROM clm.claim WHERE claim_id='{own.ClaimId}'"))!;
        return (submitted.Text("clearingReference"), reference, month);
    }

    private static object PeriodOf(string month)
    {
        var first = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return new { from = first.ToString("yyyy-MM-dd"), to = first.AddMonths(1).ToString("yyyy-MM-dd") };
    }

    [Fact]
    public async Task Statement_matches_both_legs_nets_per_counterparty_and_hands_the_net_to_BIL()
    {
        var (_, _, month) = await LegsAsync(2);

        var imported = await PostAsync("/api/clm/v1/friendly-settlements/import-statement", new { period = PeriodOf(month), counterpartyCode = Insurer });
        var statement = imported["statement"]!;
        statement.Text("reconciliation").ShouldBe("MATCHED");
        statement["exceptionCount"]!.GetValue<int>().ShouldBe(0);
        statement["lines"]!.AsArray().Count.ShouldBe(2);
        statement["lines"]!.AsArray().ShouldAllBe(l => l!["matchStatus"]!.GetValue<string>() == "MATCHED");
        var net = statement["nets"]![0]!;
        net.Text("direction").ShouldBe("PAYABLE");
        Amount(net["amount"]).ShouldBe(1800m);
        net.Text("status").ShouldBe("PENDING");
        var statementId = statement.Text("fsStatementId");
        net.Text("statementRef").ShouldBe(statementId);

        // The FS recovery carries the statement; the receivable leg's system set is recorded from the line; the CLEARING payable is issued.
        (await _money.ScalarAsync<string>($"SELECT fs_statement_id::text FROM clm.recovery WHERE type='FRIENDLY_SETTLEMENT' AND counterparty_party_id='{Insurer}' AND fs_statement_id IS NOT NULL")).ShouldBe(statementId);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.transaction_set WHERE evidence_kind='FS_STATEMENT_LINE' AND status IN ('APPROVED','POSTED') AND evidence_ref IN (SELECT fs_statement_line_id::text FROM clm.fs_statement_line WHERE fs_statement_id='{statementId}')")).ShouldBe(1);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim_payment WHERE fs_statement_id='{statementId}' AND method='CLEARING' AND status='ISSUED'")).ShouldBe(1);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type='PaymentIssued' AND payload::text LIKE '%{statementId}%'")).ShouldBe(1);

        // Re-importing the same statement changes nothing.
        var again = await PostAsync("/api/clm/v1/friendly-settlements/import-statement", new { period = PeriodOf(month), counterpartyCode = Insurer });
        again["statement"]!["lines"]!.AsArray().Count.ShouldBe(2);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.transaction_set WHERE evidence_kind='FS_STATEMENT_LINE' AND evidence_ref IN (SELECT fs_statement_line_id::text FROM clm.fs_statement_line WHERE fs_statement_id='{statementId}')")).ShouldBe(1);

        var (getResponse, got) = await _money.SendAsync(HttpMethod.Get, $"/api/clm/v1/friendly-settlements/statements/{statementId}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK, got?.ToJsonString());

        // A net that is not the computed one is stale; the right one goes to BIL once, with lines that sum to the net.
        var version = again["statement"]!["recordVersion"]!.GetValue<int>();
        await PostAsync("/api/clm/v1/friendly-settlements/approve-net",
            new { fsStatementId = statementId, counterpartyCode = Insurer, expectedRecordVersion = version, expectedNet = Money(1700m) }, HttpStatusCode.Conflict);
        var approved = await PostAsync("/api/clm/v1/friendly-settlements/approve-net",
            new { fsStatementId = statementId, counterpartyCode = Insurer, expectedRecordVersion = version, expectedNet = Money(1800m) });
        approved.Text("outcome").ShouldBe("REQUESTED");
        approved.Text("net.status").ShouldBe("REQUESTED");
        approved.Text("net.bilReferenceType").ShouldBe("DISBURSEMENT");
        var request = _disbursements.Requested.ShouldHaveSingleItem();
        request.SourceType.ShouldBe("FS_CLEARING");
        request.Method.ShouldBe("CLEARING");
        request.StatementReference.ShouldBe(statementId);
        request.SourceId.ShouldBe(statementId);
        request.PayeePartyId!.Value.Value.ShouldBe(Guid.Parse(Insurer));
        request.PayeeAccountId.ShouldBe(ClearingAccount);
        request.Amount.Amount.ShouldBe(1800m);
        request.Lines!.Sum(l => l.Amount.Amount).ShouldBe(1800m);
        request.ApprovalContentHash.ShouldBe(DisbursementContent.Hash("FS_CLEARING", statementId, request.PayeePartyId.Value, ClearingAccount, request.Amount));

        // Approving again is an illegal transition.
        await PostAsync("/api/clm/v1/friendly-settlements/approve-net",
            new { fsStatementId = statementId, counterpartyCode = Insurer, expectedRecordVersion = version + 1, expectedNet = Money(1800m) }, HttpStatusCode.UnprocessableEntity);

        // BIL reports the release refused: the net is Pending again and can be re-approved (REQ-CLM-264).
        await RunAsync(statementId, _disbursements.LastId, rejected: true);
        (await _money.ScalarAsync<string>($"SELECT status FROM clm.fs_statement WHERE fs_statement_id='{statementId}'")).ShouldBe("PENDING");
        var fresh = await _money.ScalarAsync<int>($"SELECT sum(record_version) FROM clm.fs_statement WHERE period='{month}'");
        var reapproved = await PostAsync("/api/clm/v1/friendly-settlements/approve-net",
            new { fsStatementId = statementId, counterpartyCode = Insurer, expectedRecordVersion = fresh, expectedNet = Money(1800m) });
        reapproved.Text("outcome").ShouldBe("REQUESTED");

        // BIL clears the cash: the net is Settled, cases reconciled, the CLEARING payment Cleared, the recovery closed.
        await RunAsync(statementId, _disbursements.LastId, rejected: false);
        (await _money.ScalarAsync<string>($"SELECT status FROM clm.fs_statement WHERE fs_statement_id='{statementId}'")).ShouldBe("SETTLED");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.fs_case WHERE status='RECONCILED' AND counterparty_insurer_party_id='{Insurer}'")).ShouldBe(2);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.claim_payment WHERE fs_statement_id='{statementId}' AND status='CLEARED'")).ShouldBe(1);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.recovery WHERE type='FRIENDLY_SETTLEMENT' AND fs_statement_id='{statementId}' AND status='CLOSED'")).ShouldBe(1);
    }

    [Fact]
    public async Task Unmatched_line_is_an_exception_that_blocks_the_net()
    {
        var (_, _, month) = await LegsAsync(40);
        // A case whose submitted value differs from the statement line cannot match: change the stored clearing value of the own case.
        await database.ExecuteAsSuperuserAsync($"UPDATE clm.fs_case SET clearing_value = 1100.00 WHERE role='OWN_INSURER' AND counterparty_insurer_party_id='{Insurer}'", ClaimsSlice.Ct);

        var imported = await PostAsync("/api/clm/v1/friendly-settlements/import-statement", new { period = PeriodOf(month), counterpartyCode = Insurer });
        var statement = imported["statement"]!;
        statement.Text("reconciliation").ShouldBe("EXCEPTIONS_OPEN");
        statement["exceptionCount"]!.GetValue<int>().ShouldBe(1);
        var exception = statement["lines"]!.AsArray().Single(l => l!["matchStatus"]!.GetValue<string>() != "MATCHED")!;
        exception.Text("matchStatus").ShouldBe("DIFFERENCE");
        Amount(exception["difference"]).ShouldBe(100m);

        await PostAsync("/api/clm/v1/friendly-settlements/approve-net",
            new
            {
                fsStatementId = statement.Text("fsStatementId"), counterpartyCode = Insurer, expectedRecordVersion = statement["recordVersion"]!.GetValue<int>(),
                expectedNet = Money(1900m),
            }, HttpStatusCode.UnprocessableEntity);
        _disbursements.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task Period_must_be_a_calendar_month()
    {
        await PostAsync("/api/clm/v1/friendly-settlements/import-statement",
            new { period = new { from = "2026-10-02", to = "2026-11-02" } }, HttpStatusCode.BadRequest);
    }

    /// <summary>The BIL consumer path: <c>DisbursementRejected</c> / <c>DisbursementCleared</c> of the net, handled in a scope like the worker would.</summary>
    private async Task RunAsync(string statementId, DisbursementId disbursementId, bool rejected)
    {
        #pragma warning disable CA2000 // the scope is left to the host: disposing it after a manual commit trips DbSession's own teardown order
        var scope = _slice.Factory.Services.CreateAsyncScope();
#pragma warning restore CA2000
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.Actor = ActorRef.Service("bil-test");
        context.Roles = [];
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        _ = services.GetRequiredService<CoreIns.Modules.Claims.Persistence.ClaimsDbContext>();
        var session = services.GetRequiredService<DbSession>();
        await session.BeginTransactionAsync(Ct);
        if (rejected)
        {
            await services.GetRequiredService<FsDisbursementRejectedHandler>().HandleAsync(null!, new DisbursementRejectedV1
            {
                DisbursementId = disbursementId, SourceType = "FS_CLEARING", SourceId = statementId, ReasonCode = "RELEASE_REJECTED",
                RejectingParty = DisbursementRejectedV1.RejectingPartyValue.Bank, StatementRef = statementId, Method = "CLEARING",
            }, Ct);
        }
        else
        {
            await services.GetRequiredService<FsDisbursementClearedHandler>().HandleAsync(null!, new DisbursementClearedV1
            {
                DisbursementId = disbursementId, SourceType = "FS_CLEARING", SourceId = statementId,
                Amount = new Money(1800m, Currency.EUR), ValueDate = new BusinessDate(DateOnly.FromDateTime(DateTime.UtcNow)), Method = "CLEARING",
            }, Ct);
        }

        await session.CommitAsync(Ct);
    }

    private static CancellationToken Ct => ClaimsSlice.Ct;

    private sealed class FakeDisbursements : IBillingDisbursementService
    {
        public List<DisbursementRequestRequest> Requested { get; } = [];

        public DisbursementId LastId { get; private set; }

        public Task<DisbursementRequestResponse> RequestAsync(DisbursementRequestRequest request, CommandOptions options, CancellationToken cancellationToken = default)
        {
            Requested.Add(request);
            LastId = DisbursementId.New();
            return Task.FromResult(new DisbursementRequestResponse
            {
                DisbursementId = LastId, Status = DisbursementRequestResponse.StatusValue.PendingApproval, SourceType = request.SourceType, SourceId = request.SourceId,
                Amount = request.Amount, Method = request.Method, StatementReference = request.StatementReference, Lines = request.Lines,
            });
        }

        public Task<DisbursementApproveReleaseResponse> ApproveReleaseAsync(DisbursementApproveReleaseRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DisbursementGetResponse> GetAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DisbursementListPage> ListAsync(string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DisbursementStopResponse> StopAsync(DisbursementStopRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DisbursementVoidResponse> VoidAsync(DisbursementVoidRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
