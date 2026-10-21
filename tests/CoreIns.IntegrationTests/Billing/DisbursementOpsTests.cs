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
/// SL4-BIL-DISBOPS: stop, void, bank return and bank reject of disbursements (D-SL4-13, REQ-BIL-208, -210, -361), with the
/// Development-only manual stub bank holding payments at Released so they can be corrected. Reversal entries restore the
/// source payable (LA-17 for claim payments); a return after clearing also gives the cash back (LA-10).
/// </summary>
public sealed class DisbursementOpsTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private DisbursementSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _slice = new DisbursementSlice(database, manualBank: true);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private async Task<DisbursementRequestResponse> ReleasedPaymentAsync(decimal amount = 400000m)
    {
        var party = PartyId.New();
        var account = await _slice.CreateAccountAsync(party, DisbursementSlice.NewIban());
        var response = await _slice.RequestAsync(DisbursementSlice.ClaimPayment(party, account.PayeeAccountId, amount));
        response.Status.ShouldBe(DisbursementRequestResponse.StatusValue.Released, "the manual stub bank holds the payment at Released");
        return response;
    }

    private async Task<HttpStatusCode> BankAsync(DisbursementId id, string action, string? query = null)
    {
        var (response, _) = await _slice.PostAsync($"/dev/bank/disbursements/{id.Value}/{action}{query}", new { }, roles: "Staff.BillingManager");
        return response.StatusCode;
    }

    private async Task<string?> BankProblemAsync(DisbursementId id, string action)
    {
        var (_, body) = await _slice.PostAsync($"/dev/bank/disbursements/{id.Value}/{action}", new { }, roles: "Staff.BillingManager");
        return body.Text("code");
    }

    private async Task<string> StateAsync(DisbursementId id) => await _slice.ScalarAsync<string>($"SELECT state FROM bil.disbursement WHERE disbursement_id = '{id.Value}'");

    private Task<List<string>> ReversalLinesAsync(DisbursementId id) => _slice.TextsAsync(
        $"""
        SELECT l.side || ':' || l.account_code || ':' || l.amount::numeric(19,2)
        FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
        WHERE e.disbursement_id = '{id.Value}' AND e.entry_type = 'DISBURSEMENT_REVERSED' ORDER BY l.line_no
        """);

    private Task<long> EventsAsync(string type, DisbursementId id) =>
        _slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = '{type}' AND payload::text LIKE '%{id.Value}%'");

    private async Task<DisbursementVoidResponse> VoidAsync(DisbursementId id, string roles = "Staff.ClaimsManager")
    {
        await using var scope = _slice.Scope(roles);
        return await scope.ServiceProvider.GetRequiredService<IBillingDisbursementService>().VoidAsync(
            new DisbursementVoidRequest { DisbursementId = id, Reason = "WRONG_PAYEE" }, CommandOptions.New(), Ct);
    }

    private async Task<DisbursementStopResponse> StopAsync(DisbursementId id, string roles = "Staff.ClaimsManager")
    {
        await using var scope = _slice.Scope(roles);
        return await scope.ServiceProvider.GetRequiredService<IBillingDisbursementService>().StopAsync(
            new DisbursementStopRequest { DisbursementId = id, Reason = "CLAIMANT_REQUEST" }, CommandOptions.New(), Ct);
    }

    [Fact]
    public async Task NX_EC_09_stop_and_void_are_limited_by_the_amount_of_the_actors_authority()
    {
        var large = await ReleasedPaymentAsync(150000m);

        // A claims handler holds the role but not authority over EUR 150000: refused, and the payment is untouched.
        (await Should.ThrowAsync<DomainException>(() => StopAsync(large.DisbursementId, "Staff.ClaimsHandler"))).Error.Code.Value.ShouldBe("BIL-ERR-SOD");
        (await Should.ThrowAsync<DomainException>(() => VoidAsync(large.DisbursementId, "Staff.ClaimsHandler"))).Error.Code.Value.ShouldBe("BIL-ERR-SOD");
        (await StateAsync(large.DisbursementId)).ShouldBe("RELEASED");

        // The manager's authority covers it; a handler may stop a small payment.
        (await StopAsync(large.DisbursementId)).Disbursement.Status.ShouldBe(DisbursementStopResponse.DisbursementDetail.StatusValue.Stopped);
        var small = await ReleasedPaymentAsync(900m);
        (await StopAsync(small.DisbursementId, "Staff.ClaimsHandler")).Disbursement.Status.ShouldBe(DisbursementStopResponse.DisbursementDetail.StatusValue.Stopped);
    }

    [Fact]
    public async Task REQ_BIL_208_stop_a_Released_payment_reverses_the_release_and_publishes_DisbursementStopped_once()
    {
        var released = await ReleasedPaymentAsync();
        var stopped = await StopAsync(released.DisbursementId);
        stopped.Disbursement.Status.ShouldBe(DisbursementStopResponse.DisbursementDetail.StatusValue.Stopped);
        stopped.SourcePayableRestored!.Value.Amount.ShouldBe(400000m);
        stopped.ReversalEntries!.Count.ShouldBe(1);
        (await StateAsync(released.DisbursementId)).ShouldBe("STOPPED");
        (await ReversalLinesAsync(released.DisbursementId)).ShouldBe(["DEBIT:LA-13:400000.00", "CREDIT:LA-17:400000.00"]);
        (await EventsAsync("DisbursementStopped", released.DisbursementId)).ShouldBe(1);

        var again = await Should.ThrowAsync<DomainException>(() => StopAsync(released.DisbursementId));
        again.Error.Code.Value.ShouldBe("BIL-ERR-NOT-STOPPABLE");
        (await EventsAsync("DisbursementStopped", released.DisbursementId)).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_BIL_208_an_Issued_payment_cannot_be_stopped_but_can_be_voided_and_the_claim_can_be_paid_again()
    {
        var released = await ReleasedPaymentAsync(1200m);
        (await BankAsync(released.DisbursementId, "issue")).ShouldBe(HttpStatusCode.OK);
        (await StateAsync(released.DisbursementId)).ShouldBe("ISSUED");

        var stop = await Should.ThrowAsync<DomainException>(() => StopAsync(released.DisbursementId));
        stop.Error.Code.Value.ShouldBe("BIL-ERR-NOT-STOPPABLE");

        var voided = await VoidAsync(released.DisbursementId);
        voided.Disbursement.Status.ShouldBe(DisbursementVoidResponse.DisbursementDetail.StatusValue.Voided);
        voided.ReversalEntries!.Count.ShouldBe(1);
        (await ReversalLinesAsync(released.DisbursementId)).ShouldBe(["DEBIT:LA-13:1200.00", "CREDIT:LA-17:1200.00"]);
        (await EventsAsync("DisbursementVoided", released.DisbursementId)).ShouldBe(1);

        // Voided is terminal: it cannot be voided again, and a reissue is a new disbursement for the same claim.
        (await Should.ThrowAsync<DomainException>(() => VoidAsync(released.DisbursementId))).Error.Code.Value.ShouldBe("BIL-ERR-NOT-STOPPABLE");
        var party = released.PayeePartyId!.Value;
        var reissue = DisbursementSlice.ClaimPayment(party, released.PayeeAccountId!.Value, 1200m, claim: released.ClaimId);
        (await _slice.RequestAsync(reissue)).DisbursementId.ShouldNotBe(released.DisbursementId);
    }

    [Fact]
    public async Task REQ_BIL_208_a_Cleared_payment_cannot_be_voided()
    {
        var released = await ReleasedPaymentAsync();
        await BankAsync(released.DisbursementId, "issue");
        (await BankAsync(released.DisbursementId, "clear")).ShouldBe(HttpStatusCode.OK);
        (await StateAsync(released.DisbursementId)).ShouldBe("CLEARED");
        (await Should.ThrowAsync<DomainException>(() => VoidAsync(released.DisbursementId))).Error.Code.Value.ShouldBe("BIL-ERR-NOT-STOPPABLE");
    }

    [Fact]
    public async Task REQ_BIL_210_a_bank_return_after_clearing_gives_back_the_cash_and_the_source_payable()
    {
        var released = await ReleasedPaymentAsync(750m);
        await BankAsync(released.DisbursementId, "issue");
        await BankAsync(released.DisbursementId, "clear");
        (await BankAsync(released.DisbursementId, "return", "?reason=AC04")).ShouldBe(HttpStatusCode.OK);
        (await StateAsync(released.DisbursementId)).ShouldBe("RETURNED");
        (await ReversalLinesAsync(released.DisbursementId)).ShouldBe(
            ["DEBIT:LA-10:750.00", "CREDIT:LA-13:750.00", "DEBIT:LA-13:750.00", "CREDIT:LA-17:750.00"]);
        (await EventsAsync("DisbursementReturned", released.DisbursementId)).ShouldBe(1);
        (await BankProblemAsync(released.DisbursementId, "return")).ShouldBe("BIL-ERR-NOT-STOPPABLE");
        (await EventsAsync("DisbursementReturned", released.DisbursementId)).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_BIL_361_a_bank_reject_of_a_Released_payment_restores_the_payable_and_names_the_bank()
    {
        var released = await ReleasedPaymentAsync(90m);
        (await BankAsync(released.DisbursementId, "reject", "?reason=AC01")).ShouldBe(HttpStatusCode.OK);
        (await StateAsync(released.DisbursementId)).ShouldBe("REJECTED");
        (await ReversalLinesAsync(released.DisbursementId)).ShouldBe(["DEBIT:LA-13:90.00", "CREDIT:LA-17:90.00"]);
        (await _slice.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'DisbursementRejected' AND payload::text LIKE '%{released.DisbursementId.Value}%' AND payload::text LIKE '%BANK%'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_BIL_208_HTTP_stop_and_void_need_the_permission_and_the_dev_bank_is_hidden_without_the_manual_mode()
    {
        var released = await ReleasedPaymentAsync();
        var body = new { disbursementId = released.DisbursementId.Value, reason = "CLAIMANT_REQUEST" };
        (await _slice.PostAsync("/api/bil/v1/disbursements/stop", body, roles: "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (ok, response) = await _slice.PostAsync("/api/bil/v1/disbursements/stop", body, roles: "Staff.BillingManager");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, response?.ToJsonString());
        response.Text("disbursement.status").ShouldBe("Stopped");

        var (notStoppable, problem) = await _slice.PostAsync("/api/bil/v1/disbursements/void", new { disbursementId = released.DisbursementId.Value, reason = "X" }, roles: "Staff.BillingManager");
        notStoppable.IsSuccessStatusCode.ShouldBeFalse();
        problem.Text("code").ShouldBe("BIL-ERR-NOT-STOPPABLE");

        await using var automatic = new DisbursementSlice(database);
        var (hidden, _) = await automatic.PostAsync($"/dev/bank/disbursements/{Guid.NewGuid()}/issue", new { }, roles: "Staff.BillingManager");
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
