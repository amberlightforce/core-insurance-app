using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>
/// The claim financial engine (SL2-CLM-MONEY) on PostgreSQL 17 through the real Host: real PLT (authority with the
/// illustrative D-SL2-03 grants, approvals), real BIL (payee accounts, disbursements, stub VoP and bank), real PTY
/// screening stub; POL's snapshot is the scripted sandbox double (the real-POL path is <see cref="ClaimsMoneyEndToEndTests"/>).
/// Requirement ids are in the test names.
/// </summary>
public sealed class ClaimsMoneyTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ClaimsSlice _slice = null!;
    private ClaimsMoney _money = null!;

    public ValueTask InitializeAsync()
    {
        _slice = new ClaimsSlice(database.AppConnectionString);
        _money = new ClaimsMoney(_slice.Factory, _slice.Client, database.SuperuserConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private Task<MoneyClaim> ClaimAsync() => _money.OpenClaimAsync(ClaimsSlice.Fnol(_slice.Policy()));

    private Task<string> EventsAsync(MoneyClaim claim) => _money.ScalarAsync<string>(
        $"SELECT string_agg(event_type || ':' || aggregate_sequence, ',' ORDER BY aggregate_sequence) FROM plt.outbox_message WHERE aggregate_type = 'Claim' AND aggregate_id = '{claim.ClaimId}'");

    [Fact]
    public async Task REQ_CLM_003_005_093_095_107_108_A_reserve_within_authority_is_approved_at_once_with_its_events_in_the_same_transaction()
    {
        var claim = await ClaimAsync();

        // Dry run: the full preview, nothing stored, no number consumed.
        var (dry, preview) = await _money.BuildAsync(claim, [Reserve(claim, 1200m)], dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, preview?.ToJsonString());
        Amount(preview!["preview"]![0]!["after"]).ShouldBe(1200m);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM clm.transaction_set WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(0);

        var (built, build) = await _money.BuildAsync(claim, [Reserve(claim, 1200m)]);
        built.StatusCode.ShouldBe(HttpStatusCode.OK, build?.ToJsonString());
        build.Text("status").ShouldBe("DRAFT");
        build.Text("set.transactions.0.txnNumber").ShouldBe(claim.ClaimNumber + "-T0001");
        Amount(build!["preview"]![0]!["before"]).ShouldBe(0m);
        Amount(build["preview"]![0]!["after"]).ShouldBe(1200m);

        var (submitted, submit) = await _money.SubmitAsync(build.Text("setId"));
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, submit?.ToJsonString());
        submit.Text("status").ShouldBe("APPROVED");
        submit.Text("authorityChecks.0.decision").ShouldBe("ALLOW");
        submit!["approvalRequestId"].ShouldBeNull();

        var financials = await _money.FinancialsAsync(claim);
        Amount(financials["totals"]!["openReserve"]).ShouldBe(1200m);
        Amount(financials["totals"]!["incurred"]).ShouldBe(1200m);
        financials.Text("balancesByLine.0.costCategory").ShouldBe("VEHICLE_REPAIR");

        // REQ-CLM-005: the set and per-line events follow the FNOL events on the claim's gap-free sequence.
        (await EventsAsync(claim)).ShouldBe("ClaimReported:1,CoverageVerified:2,ExposureCreated:3,TransactionSetApproved:4,ReserveChanged:5");
        var reserveChanged = await _money.ScalarAsync<string>(
            $"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'ReserveChanged' AND aggregate_id = '{claim.ClaimId}'");
        var payload = JsonNode.Parse(reserveChanged)!;
        payload.Text("delta.transaction.amount").ShouldBe("1200.00");
        payload.Text("delta.group.amount").ShouldBe("1200.00");
        payload.Text("newOpenAmount.functional.amount").ShouldBe("1200.00");
        payload.Text("claimId").ShouldBe(claim.ClaimId);
        payload.Text("reserveLine.category").ShouldBe("VEHICLE_REPAIR");
        payload["policyTermId"].ShouldNotBeNull();
        payload["accountingDate"].ShouldNotBeNull();
        payload["reserveLineId"].ShouldNotBeNull();

        // Same transaction: the events' record time is the set's approval transaction (no event without the approved row and vice versa).
        (await _money.ScalarAsync<long>(
                $"SELECT count(*) FROM plt.audit_event WHERE operation = 'clm.TransactionSet.submit' AND outcome = 'Succeeded' AND object_id = '{build.Text("setId")}'"))
            .ShouldBe(1);
        (await _money.ScalarAsync<string>($"SELECT three FROM (SELECT currency || functional_currency || group_currency AS three FROM clm.financial_transaction WHERE claim_id = '{claim.ClaimId}') t"))
            .ShouldBe("EUREUREUR");
    }

    [Fact]
    public async Task D_SL2_03_REQ_CLM_108_The_exact_limit_is_allowed_and_one_cent_above_refers_to_the_manager()
    {
        var claim = await ClaimAsync();
        (await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 5000.00m)])).Text("status").ShouldBe("APPROVED");

        var refer = await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 5000.01m)]);
        refer.Text("status").ShouldBe("PENDING_APPROVAL");
        refer.Text("authorityChecks.0.decision").ShouldBe("REFER");
        refer.Text("authorityChecks.0.referralRole").ShouldBe(Manager);
        var requestId = refer.Text("approvalRequestId");
        var (got, request) = await _money.SendAsync(HttpMethod.Get, $"/api/plt/v1/approval/{requestId}", roles: Manager, user: ManagerUser);
        got.StatusCode.ShouldBe(HttpStatusCode.OK);
        request.Text("request.type").ShouldBe("CLM.TRANSACTION_SET");
        request.Text("request.payloadHash").ShouldBe(refer.Text("set.contentHash"));
        request.Text("request.authority.amount.amount").ShouldBe("5000.01");
        request.Text("request.referralRole").ShouldBe(Manager);

        // A pending referral changes no balance.
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBe(5000m);

        // Above the manager's limit: DENY (D-SL2-03).
        var (built, build) = await _money.BuildAsync(claim, [Reserve(claim, 50000.01m)]);
        built.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (denied, deny) = await _money.SubmitAsync(build.Text("setId"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden, deny?.ToJsonString());
        deny.Text("code").ShouldBe("CLM-ERR-AUTHORITY");
    }

    [Fact]
    public async Task REQ_CLM_109_111_REQ_PLT_115_Maker_cannot_approve_own_referral_the_manager_approves_and_a_rejection_changes_nothing()
    {
        var claim = await ClaimAsync();
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 1200m)]);
        var refer = await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 5300m)]);
        var requestId = refer.Text("approvalRequestId");

        // Maker ≠ checker (PLT): the handler cannot decide its own request, even holding the manager role.
        var (own, ownBody) = await _money.DecideAsync(requestId, roles: $"{Handler},{Manager}", user: HandlerUser);
        own.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        ownBody.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");

        // Another handler lacks the authority.
        var (other, otherBody) = await _money.DecideAsync(requestId, roles: Handler, user: "handler-petros");
        other.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        otherBody.Text("code").ShouldBe("PLT-ERR-AUTHORITY-REFERRAL-REQUIRED");

        var (decided, decision) = await _money.DecideAsync(requestId);
        decided.StatusCode.ShouldBe(HttpStatusCode.OK, decision?.ToJsonString());
        await _money.DrainAsync();

        var set = await _money.SetAsync(refer.Text("setId"));
        set.Text("status").ShouldBe("APPROVED");
        set.Text("fourEyes").ShouldBe("true");
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBe(6500m);
        (await EventsAsync(claim)).ShouldEndWith("TransactionSetApproved:6,ReserveChanged:7");
        (await _money.ScalarAsync<string>(
                $"SELECT payload->'newOpenAmount'->'transaction'->>'amount' FROM plt.outbox_message WHERE event_type = 'ReserveChanged' AND aggregate_id = '{claim.ClaimId}' AND aggregate_sequence = 7"))
            .ShouldBe("6500.00");

        // A rejected referral: Rejected with its transactions, balances unchanged, TransactionSetRejected (REQ-CLM-111).
        var second = await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 9000m)]);
        (await _money.DecideAsync(second.Text("approvalRequestId"), "Reject")).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();
        var rejected = await _money.SetAsync(second.Text("setId"));
        rejected.Text("status").ShouldBe("REJECTED");
        rejected.Text("rejectionReason").ShouldBe("APPROVER_REJECTED");
        rejected.Text("transactions.0.status").ShouldBe("REJECTED");
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBe(6500m);
        (await EventsAsync(claim)).ShouldEndWith("ReserveChanged:7,TransactionSetRejected:8");
    }

    [Fact]
    public async Task REQ_CLM_112_A_set_changed_under_it_is_stale_at_submit_and_at_approval_and_never_applied()
    {
        var claim = await ClaimAsync();
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 1200m)]);
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 5000m)]);
        var (account, _) = await _money.CaptureAsync(claim);

        // Stale at submit: the line changed between build and submit.
        var (built, draft) = await _money.BuildAsync(claim, [Payment(claim, 6000m, account)]);
        built.StatusCode.ShouldBe(HttpStatusCode.OK, draft?.ToJsonString());
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 100m)]);
        var (stale, staleBody) = await _money.SubmitAsync(draft.Text("setId"));
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, staleBody?.ToJsonString());
        staleBody.Text("code").ShouldBe("CLM-ERR-SET-STALE");

        // Stale at approval: a referred payment set, then the line changes; the manager's approval is refused by CLM.
        var pending = await _money.BuildAndSubmitAsync(claim, [Payment(claim, 6200m, account)]);
        pending.Text("status").ShouldBe("PENDING_APPROVAL");
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, -50m)]);
        (await _money.DecideAsync(pending.Text("approvalRequestId"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();

        var set = await _money.SetAsync(pending.Text("setId"));
        set.Text("status").ShouldBe("REJECTED");
        set.Text("rejectionReason").ShouldBe("SET_STALE");
        (await _money.PaymentsAsync(claim))[0].Text("status").ShouldBe("REJECTED");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM bil.disbursement WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(0);
        Amount((await _money.FinancialsAsync(claim))["totals"]!["paid"]).ShouldBe(0m);
        (await _money.ScalarAsync<string>(
                $"SELECT payload->>'reason' FROM plt.outbox_message WHERE event_type = 'TransactionSetRejected' AND aggregate_id = '{claim.ClaimId}'"))
            .ShouldBe("SET_STALE");
    }

    [Fact]
    public async Task REQ_CLM_112_Racing_approvals_and_a_replayed_decision_have_one_effect()
    {
        var claim = await ClaimAsync();
        var refer = await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 7000m)]);
        var requestId = refer.Text("approvalRequestId");

        var results = await Task.WhenAll(_money.DecideAsync(requestId), _money.DecideAsync(requestId, user: "manager-maria"));
        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join(" | ", results.Select(r => r.Body?.ToJsonString())));
        results.Single(r => r.Response.StatusCode != HttpStatusCode.OK).Body.Text("code").ShouldBe("PLT-ERR-APPROVAL-STALE");
        await _money.DrainAsync();

        // Replay the ApprovalDecided delivery (dead-letter replay, D-ARC-26): no second effect.
        var services = _slice.Factory.Services;
        var registration = services.GetRequiredService<EventHandlerRegistry>().Find("CLM.ApprovalDecided.ApplyTransactionSet")!;
        var envelope = await EnvelopeAsync(requestId);
        (await new HandlerInvoker(services.GetRequiredService<IServiceScopeFactory>()).InvokeAsync(registration, envelope, replay: true, services.GetRequiredService<IClock>(), Ct))
            .ShouldBeTrue();
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'TransactionSetApproved' AND aggregate_id = '{claim.ClaimId}'")).ShouldBe(1);
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ReserveChanged' AND aggregate_id = '{claim.ClaimId}'")).ShouldBe(1);
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBe(7000m);
    }

    private async Task<EventEnvelope> EnvelopeAsync(string requestId)
    {
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand(
            $"SELECT {EnvelopeColumnsReader.Columns} FROM plt.outbox_message WHERE event_type = 'ApprovalDecided' AND aggregate_id = '{requestId}'");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return EnvelopeColumnsReader.Read(reader);
    }

    [Fact]
    public async Task REQ_CLM_004_097_099_119_128_129_Final_payment_tops_up_releases_the_remainder_disburses_and_a_duplicate_is_refused()
    {
        var claim = await ClaimAsync();
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 1000m)]);
        var (account, iban) = await _money.CaptureAsync(claim);

        // REQ-CLM-097: payment 1,200 over open reserve 1,000 adds a reserve increase of 200 to the same set (auto-adjust on).
        var (built, build) = await _money.BuildAsync(claim, [Payment(claim, 1200m, account, paymentType: "PARTIAL")], dryRun: true);
        built.StatusCode.ShouldBe(HttpStatusCode.OK, build?.ToJsonString());
        var transactions = build!["set"]!["transactions"]!.AsArray();
        transactions.Count.ShouldBe(2);
        transactions[1]!.Text("kind").ShouldBe("RESERVE");
        transactions[1]!.Text("reasonCode").ShouldBe("AUTO_ADJUST");
        Amount(transactions[1]!["amount"]).ShouldBe(200m);

        // REQ-CLM-099/119: a final payment of 800 releases the remaining 200 in the same set; within authority → disbursed at once.
        var submit = await _money.BuildAndSubmitAsync(claim, [Payment(claim, 800m, account)]);
        submit.Text("status").ShouldBe("APPROVED");
        submit.Text("set.transactions.1.reasonCode").ShouldBe("FINAL_RELEASE");
        Amount(submit["set"]!["transactions"]![1]!["amount"]).ShouldBe(-200m);
        submit.Text("payments.0.status").ShouldBe("SUBMITTED");
        submit.Text("payments.0.approvalEvidenceRef").ShouldStartWith("CLM/TransactionSet/");
        var paymentId = submit.Text("payments.0.claimPaymentId");

        // BIL disbursed with source id = claim payment id (D-SL2-12 b); CLM learns Issued/Cleared from the events.
        (await _money.ScalarAsync<string>($"SELECT source_id FROM bil.disbursement WHERE claim_id = '{claim.ClaimId}'")).ShouldBe(paymentId);
        await _money.DrainAsync();
        var payment = (await _money.PaymentsAsync(claim))[0]!;
        payment.Text("status").ShouldBe("CLEARED");
        payment.Text("maskedAccount").ShouldBe("****" + iban[^4..]);
        var issued = JsonNode.Parse(await _money.ScalarAsync<string>(
            $"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'PaymentIssued' AND aggregate_id = '{claim.ClaimId}'"))!;
        issued.Text("paymentId").ShouldBe(paymentId);
        issued.Text("amount.transaction.amount").ShouldBe("800.00");
        issued.Text("lines.0.eroding").ShouldBe("true");
        issued.Text("lines.0.costCategory").ShouldBe("VEHICLE_REPAIR");
        issued.Text("method").ShouldBe("SEPA_CT");

        var financials = await _money.FinancialsAsync(claim);
        Amount(financials["totals"]!["openReserve"]).ShouldBe(0m);
        Amount(financials["totals"]!["paid"]).ShouldBe(800m);
        Amount(financials["totals"]!["incurred"]).ShouldBe(800m);
        financials.Text("balancesByLine.0.final").ShouldBe("true");

        // REQ-CLM-129 / D-SL2-10 a: the same payment again is refused by CLM.
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 900m)]);
        var (again, againBody) = await _money.BuildAsync(claim, [Payment(claim, 800m, account, paymentType: "PARTIAL")]);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict, againBody?.ToJsonString());
        againBody.Text("code").ShouldBe("CLM-ERR-DUPLICATE-PAYMENT");

        // R-38: the IBAN is nowhere in CLM's tables, events, audit or idempotency store.
        (await _money.ExposuresAsync(iban)).ShouldBe(0);
        (await _money.ExposuresAsync(iban[4..])).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_CLM_095_098_130_Validation_reason_category_currency_payability_and_negative_reserve()
    {
        var claim = await ClaimAsync();
        var (noReason, body) = await _money.BuildAsync(claim, [new { kind = "RESERVE", exposureId = claim.ExposureId, costType = "INDEMNITY", costCategory = "VEHICLE_REPAIR", amount = Money(100m) }]);
        noReason.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("CLM-ERR-RESERVE-REASON");

        (await _money.BuildAsync(claim, [Reserve(claim, 100m, category: "MEDICAL")])).Body.Text("code").ShouldBe("CLM-ERR-VALIDATION");
        (await _money.BuildAsync(claim, [Reserve(claim, 100m, category: "ASSESSOR_FEE", costType: "EXPENSE_ALLOCATED")])).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _money.BuildAsync(claim, [new { kind = "RESERVE", exposureId = claim.ExposureId, costType = "INDEMNITY", costCategory = "VEHICLE_REPAIR", amount = new { amount = "100.00", currency = "USD" }, reason = "X" }]))
            .Body.Text("code").ShouldBe("CLM-ERR-VALIDATION");
        (await _money.BuildAsync(claim, [Reserve(claim, -10m)])).Body.Text("code").ShouldBe("CLM-ERR-VALIDATION");

        // REQ-CLM-123/130: a payee account that was not captured on the claim is not payable.
        var (notPayable, notPayableBody) = await _money.BuildAsync(claim, [Payment(claim, 10m, Guid.NewGuid().ToString())]);
        notPayable.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, notPayableBody?.ToJsonString());
        notPayableBody.Text("code").ShouldBe("CLM-ERR-NOT-PAYABLE");

        // Payee must be on the claim (REQ-CLM-131 context).
        var (stranger, strangerBody) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/payee-accounts/capture",
            new { claimId = claim.ClaimId, partyId = Guid.NewGuid(), iban = Bil.DisbursementSlice.NewIban(), holderName = "X" });
        stranger.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, strangerBody?.ToJsonString());
        strangerBody.Text("code").ShouldBe("CLM-ERR-PAYEE-NOT-ON-CLAIM");

        // Permissions: a role without the claims grants is refused.
        (await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/transaction-sets/build", new { claimId = claim.ClaimId, transactions = new[] { Reserve(claim, 1m) } }, roles: "Staff.Underwriter"))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task REQ_CLM_072_101_The_close_guard_reads_the_derived_balances_and_financials_travel_in_record_time()
    {
        var claim = await ClaimAsync();
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 400m)]);
        var between = Instant.FromDateTimeOffset(DateTimeOffset.UtcNow).ToString();
        await Task.Delay(20, Ct);
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 100m)]);

        // REQ-CLM-101: as of the instant between the two sets, the earlier balance.
        Amount((await _money.FinancialsAsync(claim, between))["totals"]!["openReserve"]).ShouldBe(400m);
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBe(500m);

        var version = int.Parse((await _money.ClaimAsync(claim)).Text("summary.recordVersion"), System.Globalization.CultureInfo.InvariantCulture);
        var (blocked, blockedBody) = await _money.CloseAsync(claim, version);
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, blockedBody?.ToJsonString());
        blockedBody.Text("code").ShouldBe("CLM-ERR-CLOSE-GUARD");

        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, -500m, reason: "RELEASE")]);
        var (closed, closedBody) = await _money.CloseAsync(claim, version);
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, closedBody?.ToJsonString());

        // A closed claim takes no financial transaction.
        (await _money.BuildAsync(claim, [Reserve(claim, 10m)])).Body.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task D_SL2_10_d_BIL_verifies_the_PLT_approval_named_as_evidence()
    {
        var claim = await ClaimAsync();
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 7000m)]);
        var (account, _) = await _money.CaptureAsync(claim);
        var pending = await _money.BuildAndSubmitAsync(claim, [Payment(claim, 6000m, account, paymentType: "PARTIAL")]);
        var requestId = Guid.Parse(pending.Text("approvalRequestId"));
        var paymentId = pending.Text("payments.0.claimPaymentId");
        var payee = new PartyId(Guid.Parse(claim.InsuredPartyId));
        var amount = new CoreIns.SharedKernel.Money(6000m, Currency.EUR);
        DisbursementRequestRequest Request(decimal paid) => new()
        {
            SourceType = DisbursementCodes.ClaimPayment, SourceId = paymentId, PayeePartyId = payee, PayeeAccountId = Guid.Parse(account),
            Amount = new CoreIns.SharedKernel.Money(paid, Currency.EUR), Method = DisbursementCodes.SepaCreditTransfer,
            ApprovalEvidenceRef = $"PLT/ApprovalRequest/{requestId:D}",
            ApprovalContentHash = DisbursementContent.Hash(DisbursementCodes.ClaimPayment, paymentId, payee, Guid.Parse(account), new CoreIns.SharedKernel.Money(paid, Currency.EUR)),
            ClaimId = new ClaimId(Guid.Parse(claim.ClaimId)),
        };

        async Task<string> RefusedAsync(DisbursementRequestRequest request)
        {
            await using var scope = Rating.RatingTestSupport.Scope(_slice.Factory.Services, Handler);
            var error = await Should.ThrowAsync<DomainException>(() =>
                scope.ServiceProvider.GetRequiredService<IBillingDisbursementService>().RequestAsync(request, CommandOptions.New(), Ct));
            return error.Error.Code.Value;
        }

        // Not yet approved, and a different amount than approved: both refused.
        (await RefusedAsync(Request(6000m))).ShouldBe("BIL-ERR-APPROVAL-MISMATCH");
        (await _money.DecideAsync(pending.Text("approvalRequestId"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefusedAsync(Request(5999m))).ShouldBe("BIL-ERR-APPROVAL-MISMATCH");

        // CLM's own execution (after ApprovalDecided) passes BIL's verification.
        await _money.DrainAsync();
        var payment = (await _money.PaymentsAsync(claim))[0]!;
        payment.Text("status").ShouldBe("CLEARED");
        payment.Text("approvalEvidenceRef").ShouldBe($"PLT/ApprovalRequest/{requestId:D}");
        amount.Amount.ShouldBe(6000m);
    }
}
