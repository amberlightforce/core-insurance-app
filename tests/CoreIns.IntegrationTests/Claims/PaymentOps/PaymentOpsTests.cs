using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims.PaymentOps;

/// <summary>
/// SL4-CLM-PAYOPS (REQ-CLM-125..127, -136, -138) over the real Claims/Platform/BIL/CMP pipeline and database. The BIL side of void and
/// stop (<c>bil.Disbursement.void/stop</c>) is another work package; here BIL's correction facts are delivered the way the worker does.
/// </summary>
public sealed class PaymentOpsTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    /// <summary>A claim with a 1,000 reserve and one 800 PARTIAL payment, cleared by the stub bank.</summary>
    private async Task<(MoneyClaim Claim, string PaymentId, string AccountId)> PaidClaimAsync()
    {
        var claim = await ClaimAsync();
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 1000m)]);
        var (account, _) = await _money.CaptureAsync(claim);
        var submit = await _money.BuildAndSubmitAsync(claim, [Payment(claim, 800m, account, paymentType: "PARTIAL")]);
        submit.Text("payments.0.status").ShouldBe("SUBMITTED");
        await _money.DrainAsync();
        return (claim, submit.Text("payments.0.claimPaymentId"), account);
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string action, object body) =>
        _money.SendAsync(HttpMethod.Post, $"/api/clm/v1/payments/{action}", body);

    /// <summary>BIL's <c>DisbursementReturned</c> for the claim's disbursement, handled in a scope like the worker would.</summary>
    private async Task ReturnedAsync(MoneyClaim claim, string paymentId)
    {
        var disbursement = Guid.Parse(await _money.ScalarAsync<string>($"SELECT disbursement_id::text FROM bil.disbursement WHERE source_id = '{paymentId}'"));
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
        await services.GetRequiredService<CoreIns.Modules.Claims.Events.DisbursementReturnedHandler>().HandleAsync(null!, new DisbursementReturnedV1
        {
            DisbursementId = new DisbursementId(disbursement), SourceType = DisbursementCodes.ClaimPayment, SourceId = paymentId, ReasonCode = "ACCOUNT_CLOSED",
        }, Ct);
        await session.CommitAsync(Ct);
    }

    [Fact]
    public async Task REQ_CLM_138_An_issued_payment_requests_one_settlement_receipt_and_stores_its_mark()
    {
        var (claim, paymentId, _) = await PaidClaimAsync();
        await _money.DrainAsync();

        // One CMP document per payment: source CLM_CLAIM_PAYMENT, category CLAIM_SETTLEMENT_RECEIPT, the claim payment id as source.
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM cmp.fiscal_document WHERE source_type = 'CLM_CLAIM_PAYMENT' AND source_id = '{paymentId}' AND role = 'ISSUE'"))
            .ShouldBe(1);
        var payment = (await _money.PaymentsAsync(claim))[0]!;
        payment.Text("fiscalMark").ShouldStartWith("STUB-");
    }

    [Fact]
    public async Task REQ_CLM_125_127_136_A_returned_payment_is_reversed_once_restores_the_reserve_and_is_reissued_to_a_new_account()
    {
        var (claim, paymentId, _) = await PaidClaimAsync();
        var before = await _money.FinancialsAsync(claim);
        Amount(before["totals"]!["paid"]).ShouldBe(800m);
        Amount(before["totals"]!["openReserve"]).ShouldBe(200m);

        await ReturnedAsync(claim, paymentId);
        await ReturnedAsync(claim, paymentId); // replayed delivery: no second effect
        await _money.DrainAsync();

        var payment = (await _money.PaymentsAsync(claim))[0]!;
        payment.Text("status").ShouldBe("RETURNED");
        var after = await _money.FinancialsAsync(claim);
        Amount(after["totals"]!["paid"]).ShouldBe(0m);
        Amount(after["totals"]!["openReserve"]).ShouldBe(1000m); // an eroding payment's reversal restores the open reserve

        // A linked negative transaction in its own approved system set; the original is never edited.
        (await _money.ScalarAsync<long>(
            $"SELECT count(*) FROM clm.financial_transaction r JOIN clm.financial_transaction o ON o.txn_id = r.reverses_txn_id WHERE r.claim_id = '{claim.ClaimId}' AND r.amount = -o.amount AND r.amount = -800.00"))
            .ShouldBe(1);
        (await _money.ScalarAsync<string>($"SELECT s.status FROM clm.financial_transaction r JOIN clm.transaction_set s ON s.set_id = r.set_id WHERE r.claim_id = '{claim.ClaimId}' AND r.reverses_txn_id IS NOT NULL"))
            .ShouldBe("APPROVED");

        // PaymentVoided once, reason RETURNED, full amount and the issued lines (positive).
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PaymentVoided' AND aggregate_id = '{claim.ClaimId}'")).ShouldBe(1);
        var voided = JsonNode.Parse(await _money.ScalarAsync<string>($"SELECT payload::text FROM plt.outbox_message WHERE event_type = 'PaymentVoided' AND aggregate_id = '{claim.ClaimId}'"))!;
        voided.Text("paymentId").ShouldBe(paymentId);
        voided.Text("reason").ShouldBe("RETURNED");
        voided.Text("amount.transaction.amount").ShouldBe("800.00");
        voided.Text("lines.0.eroding").ShouldBe("true");
        voided["reversalTransactionIds"]!.AsArray().Count.ShouldBe(1);

        // Reissue to a newly captured account (the original account's payment is not a duplicate any more).
        var (newAccount, _) = await _money.CaptureAsync(claim);
        var (reissued, body) = await PostAsync("reissue", new { paymentId, reason = "PAYEE_ACCOUNT_REJECTED", payeeAccountId = newAccount });
        reissued.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("originalPaymentId").ShouldBe(paymentId);
        var newId = body.Text("newPaymentId");
        newId.ShouldNotBe(paymentId);
        (await _money.ScalarAsync<string>($"SELECT reissue_of::text FROM clm.claim_payment WHERE claim_payment_id = '{newId}'")).ShouldBe(paymentId);
        (await _money.ScalarAsync<string>($"SELECT payee_account_id::text FROM clm.claim_payment WHERE claim_payment_id = '{newId}'")).ShouldBe(newAccount);
        await _money.DrainAsync();
        (await _money.PaymentsAsync(claim)).AsArray().Single(p => p.Text("claimPaymentId") == newId).Text("status").ShouldBe("ON_HOLD"); // a freshly captured account is in BIL's cooling-off
        Amount((await _money.FinancialsAsync(claim))["totals"]!["paid"]).ShouldBe(800m);

        // At most once, and only from a corrected state.
        var (twice, twiceBody) = await PostAsync("reissue", new { paymentId, reason = "PAYEE_ACCOUNT_REJECTED", payeeAccountId = newAccount });
        twice.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, twiceBody?.ToJsonString());
        twiceBody.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");
        var (live, liveBody) = await PostAsync("reissue", new { paymentId = newId, reason = "WRONG_AMOUNT" });
        live.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, liveBody?.ToJsonString());
    }

    [Fact]
    public async Task REQ_CLM_126_A_cleared_payment_is_not_stoppable_and_cannot_be_voided()
    {
        var (_, paymentId, _) = await PaidClaimAsync();

        var (stop, stopBody) = await PostAsync("stop", new { paymentId, reason = "ISSUED_IN_ERROR" });
        stop.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, stopBody?.ToJsonString());
        stopBody.Text("code").ShouldBe("CLM-ERR-NOT-STOPPABLE");

        var (voided, voidBody) = await PostAsync("void", new { paymentId, reason = "ISSUED_IN_ERROR" });
        voided.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, voidBody?.ToJsonString());
        voidBody.Text("code").ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");

        var (missing, _) = await PostAsync("void", new { paymentId = Guid.NewGuid(), reason = "ISSUED_IN_ERROR" });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task REQ_CLM_125_A_payment_awaiting_approval_is_voided_inside_CLM_and_can_be_reissued()
    {
        var claim = await ClaimAsync();
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 5000m)]);
        await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 3000m)], Manager, ManagerUser);
        var (account, _) = await _money.CaptureAsync(claim);
        var referred = await _money.BuildAndSubmitAsync(claim, [Payment(claim, 6200m, account, paymentType: "PARTIAL")]);
        referred.Text("status").ShouldBe("PENDING_APPROVAL");
        var paymentId = referred.Text("payments.0.claimPaymentId");

        var (voided, body) = await PostAsync("void", new { paymentId, reason = "WRONG_AMOUNT" });
        voided.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("status").ShouldBe("VOIDED");
        (await _money.SetAsync(referred.Text("setId"))).Text("status").ShouldBe("REJECTED");
        (await _money.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PaymentVoided' AND aggregate_id = '{claim.ClaimId}'")).ShouldBe(0); // never issued
        Amount((await _money.FinancialsAsync(claim))["totals"]!["paid"]).ShouldBe(0m);

        var (reissued, reissue) = await PostAsync("reissue", new { paymentId, reason = "WRONG_AMOUNT" });
        reissued.StatusCode.ShouldBe(HttpStatusCode.OK, reissue?.ToJsonString());
        reissue.Text("originalPaymentId").ShouldBe(paymentId);
        // The engine ran every check again: the new payment is over the handler's limit, so it is referred, not paid.
        (await _money.SetAsync(reissue.Text("setId"))).Text("status").ShouldBe("PENDING_APPROVAL");
    }
}
