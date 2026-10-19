using System.Net;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil;

/// <summary>
/// SL4-BIL-FSOUT: the FS_CLEARING net payable to the clearing office (D-SL4-02, -12): lines that sum to the net, method CLEARING
/// for this source only, the release approval by a second user, and the FS_CLEARING_RELEASED / _CLEARED entries (LA-24, LA-13,
/// LA-10) carrying the statement reference and the counterparty.
/// </summary>
public sealed class FsClearingOutTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private DisbursementSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _slice = new DisbursementSlice(database);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private async Task<(DisbursementRequestRequest Request, PartyId Office)> FsRequestAsync(decimal first = 1500m, decimal second = 300m, decimal? net = null)
    {
        var office = PartyId.New();
        var account = await _slice.CreateAccountAsync(office, DisbursementSlice.NewIban(), PayeeAccountCreateRequest.PurposeValue.Clearing);
        var source = Guid.CreateVersion7().ToString();
        var amount = new Money(net ?? first + second, Currency.EUR);
        return (new DisbursementRequestRequest
        {
            SourceType = DisbursementCodes.FsClearing,
            SourceId = source,
            PayeePartyId = office,
            PayeeAccountId = account.PayeeAccountId,
            Amount = amount,
            Method = DisbursementCodes.Clearing,
            ApprovalEvidenceRef = "CLM/FsNet/" + source,
            ApprovalContentHash = DisbursementContent.Hash(DisbursementCodes.FsClearing, source, office, account.PayeeAccountId, amount),
            StatementReference = "FS-2026-10-" + source[^6..],
            Lines =
            [
                new FsClearingLine { ClaimId = ClaimId.New(), FsCaseId = FsCaseId.New(), Amount = new Money(first, Currency.EUR) },
                new FsClearingLine { ClaimId = ClaimId.New(), FsCaseId = FsCaseId.New(), Amount = new Money(second, Currency.EUR) },
            ],
        }, office);
    }

    private async Task<DisbursementApproveReleaseResponse> DecideAsync(DisbursementId id, DisbursementApproveReleaseRequest.DecisionValue decision, string actor = "fs-billing-manager", string? reason = null)
    {
        await using var scope = _slice.Scope("Staff.BillingManager");
        scope.ServiceProvider.GetRequiredService<RequestContext>().Actor = ActorRef.User(actor);
        return await scope.ServiceProvider.GetRequiredService<IBillingDisbursementService>().ApproveReleaseAsync(
            new DisbursementApproveReleaseRequest { DisbursementId = id, Decision = decision, Reason = reason }, CommandOptions.New(), Ct);
    }

    [Fact]
    public async Task REQ_BIL_355_357_FS_net_waits_for_a_second_users_release_then_posts_LA24_LA13_LA10_with_statement_and_counterparty()
    {
        var (request, office) = await FsRequestAsync();
        var requested = await _slice.RequestAsync(request);
        requested.Status.ShouldBe(DisbursementRequestResponse.StatusValue.PendingApproval);
        requested.StatementReference.ShouldBe(request.StatementReference);
        requested.Lines!.Count.ShouldBe(2);
        requested.Lines.Sum(l => l.Amount.Amount).ShouldBe(1800m);
        requested.ReleaseApproval!.Required.ShouldBeTrue();
        requested.Method.ShouldBe("CLEARING");
        var id = requested.DisbursementId.Value;
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{id}'")).ShouldBe(0, "nothing is posted before the release approval");

        var decided = await DecideAsync(requested.DisbursementId, DisbursementApproveReleaseRequest.DecisionValue.Approve);
        decided.Disbursement.Status.ShouldBe(DisbursementRequestResponse.StatusValue.Cleared);
        (await _slice.ScalarAsync<string>($"SELECT release_approved_by FROM bil.disbursement WHERE disbursement_id = '{id}'")).ShouldContain("fs-billing-manager");

        var lines = await _slice.TextsAsync(
            $"""
            SELECT e.entry_type || ':' || l.side || ':' || l.account_code || ':' || l.amount::numeric(19,2) || ':' || l.statement_ref || ':' || l.counterparty_party_id
            FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
            WHERE e.disbursement_id = '{id}' ORDER BY e.recorded_at, e.entry_type DESC, l.line_no
            """);
        var tail = $":1800.00:{request.StatementReference}:{office.Value}";
        lines.ShouldBe(
        [
            "FS_CLEARING_RELEASED:DEBIT:LA-24" + tail,
            "FS_CLEARING_RELEASED:CREDIT:LA-13" + tail,
            "FS_CLEARING_CLEARED:DEBIT:LA-13" + tail,
            "FS_CLEARING_CLEARED:CREDIT:LA-10" + tail,
        ]);

        // Read model: GET and the inbox list carry the FS fields.
        var (response, body) = await _slice.GetAsync($"/api/bil/v1/disbursements/{id}", roles: "Staff.BillingManager");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.Text("disbursement.statementReference").ShouldBe(request.StatementReference);
        var (listResponse, list) = await _slice.GetAsync("/api/bil/v1/disbursements", roles: "Staff.BillingManager");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        list!["items"]!.AsArray().Select(i => i!["disbursement"]!["sourceType"]!.GetValue<string>()).ShouldContain("FS_CLEARING");
    }

    [Fact]
    public async Task REQ_BIL_357_the_requester_cannot_release_and_the_HTTP_route_needs_the_billing_manager_role()
    {
        var (request, _) = await FsRequestAsync();
        var requested = await _slice.RequestAsync(request); // actor: rating-test
        var error = await Should.ThrowAsync<DomainException>(() => DecideAsync(requested.DisbursementId, DisbursementApproveReleaseRequest.DecisionValue.Approve, actor: "rating-test"));
        error.Error.Code.Value.ShouldBe("BIL-ERR-SOD");
        (await _slice.ScalarAsync<string>($"SELECT state FROM bil.disbursement WHERE disbursement_id = '{requested.DisbursementId.Value}'")).ShouldBe("PENDING_APPROVAL");

        var body = new { disbursementId = requested.DisbursementId.Value, decision = "APPROVE" };
        (await _slice.PostAsync("/api/bil/v1/disbursements/approve-release", body, roles: "Staff.Billing")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (ok, approved) = await _slice.PostAsync("/api/bil/v1/disbursements/approve-release", body, roles: "Staff.BillingManager");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, approved?.ToJsonString());
        approved.Text("decision").ShouldBe("APPROVE");

        // Decided once: a second decision finds nothing awaiting approval.
        var again = await Should.ThrowAsync<DomainException>(() => DecideAsync(requested.DisbursementId, DisbursementApproveReleaseRequest.DecisionValue.Approve));
        again.Error.Code.Value.ShouldBe("BIL-ERR-APPROVAL-REQUIRED");
    }

    [Fact]
    public async Task REQ_BIL_357_rejecting_the_release_ends_the_disbursement_without_any_ledger_entry()
    {
        var (request, _) = await FsRequestAsync();
        var requested = await _slice.RequestAsync(request);
        var rejected = await DecideAsync(requested.DisbursementId, DisbursementApproveReleaseRequest.DecisionValue.Reject, reason: "Statement disputed");
        rejected.Disbursement.Status.ShouldBe(DisbursementRequestResponse.StatusValue.Rejected);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM bil.ledger_entry WHERE disbursement_id = '{requested.DisbursementId.Value}'")).ShouldBe(0);
        (await _slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DisbursementRejected' AND payload::text LIKE '%{requested.DisbursementId.Value}%'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_BIL_354_355_FS_gates_lines_must_sum_CLEARING_is_for_FS_only_and_one_live_net_per_source()
    {
        var (request, _) = await FsRequestAsync();
        (await _slice.RefusedAsync(request with { Lines = [request.Lines![0]] })).ShouldBe("BIL-ERR-LINES-MISMATCH");
        (await _slice.RefusedAsync(request with { Method = DisbursementCodes.SepaCreditTransfer })).ShouldBe("BIL-ERR-METHOD-NOT-ALLOWED");

        var claim = DisbursementSlice.ClaimPayment(request.PayeePartyId!.Value, request.PayeeAccountId!.Value, 1800m);
        (await _slice.RefusedAsync(claim with { Method = DisbursementCodes.Clearing })).ShouldBe("BIL-ERR-SOURCE");

        var tampered = request with { Amount = new Money(1800.01m, Currency.EUR), Lines = [request.Lines![0], request.Lines[1] with { Amount = new Money(300.01m, Currency.EUR) }] };
        (await _slice.RefusedAsync(tampered)).ShouldBe("BIL-ERR-APPROVAL-MISMATCH");

        await _slice.RequestAsync(request);
        (await _slice.RefusedAsync(request)).ShouldBe("BIL-ERR-DUPLICATE");
    }
}
