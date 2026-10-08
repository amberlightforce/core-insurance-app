using System.Globalization;
using System.Net;
using CoreIns.IntegrationTests.Policy;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Claims.ClaimsMoney;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>
/// The slice money path end to end inside the monolith with every module real (PTY, PFC MOTOR-GR, MKT, RAT, UW, POL, PLT,
/// BIL with its stub VoP and bank channel, PTY's screening stub, FIN): bind a motor policy, move the (non-production,
/// shiftable) clock into the term, FNOL → exposure → reserve 1,200 (handler, ALLOW) → +5,300 (REFER, manager approves in
/// the PLT inbox) → final payment 6,200 to the insured's account (REFER, approved, BIL disbursement Cleared, PaymentIssued)
/// with the 300 remainder released → open reserve 0, incurred 6,200 → close. The worker's outbox dispatch is run by hand.
/// </summary>
public sealed class ClaimsMoneyEndToEndTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private PolicySlice _policy = null!;
    private ClaimsMoney _money = null!;

    public async ValueTask InitializeAsync()
    {
        _policy = new PolicySlice(
            database.AppConnectionString, realRatingAndUnderwriting: true, settings: new Dictionary<string, string?> { [ClockConfiguration.ModeKey] = "Shiftable" });
        await _policy.SeedAsync();
        _money = new ClaimsMoney(_policy.Factory, _policy.Client, database.SuperuserConnectionString);
    }

    public async ValueTask DisposeAsync() => await _policy.DisposeAsync();

    [Fact]
    public async Task E2E_02a_FNOL_reserve_referral_final_payment_through_BIL_release_and_close()
    {
        // A real bound policy (term starts in two days), then the clock moves ten days into the term.
        var party = await _policy.CreatePartyAsync();
        var (jobId, _, _) = await _policy.DraftAsync(party, DateTimeOffset.UtcNow.AddDays(2));
        (await _policy.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (bound, bind) = await _policy.BindAsync(jobId);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        var clock = (ShiftableClock)_policy.Factory.Services.GetRequiredService<IClock>();
        clock.Advance(TimeSpan.FromDays(10));
        await _money.DrainAsync();

        // FNOL (cover verified on POL's snapshot at the loss date) → exposure.
        var scripted = new ScriptedPolicy(Guid.Parse(bind.Text("policyId")), bind.Text("policyNumber"), Guid.Parse(party), "MOTOR-GR", clock.Now, clock.Now, []);
        var (reported, fnol) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/fnol/submit", ClaimsSlice.Fnol(scripted, lossAt: clock.Now.Plus(TimeSpan.FromDays(-1)), exposure: false));
        reported.StatusCode.ShouldBe(HttpStatusCode.OK, fnol?.ToJsonString());
        fnol.Text("claim.snapshotStatus").ShouldBe("VERIFIED");
        fnol.Text("claim.policyInForceAtLoss").ShouldBe("true");
        var (created, exposure) = await _money.SendAsync(HttpMethod.Post, "/api/clm/v1/exposures",
            new { claimId = fnol.Text("claimId"), expectedRecordVersion = 1, kind = "OWN_DAMAGE", coverageCode = "OWN-DAMAGE" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, exposure?.ToJsonString());
        exposure.Text("exposure.coverageIndication").ShouldBe("COVERED");
        var claim = new MoneyClaim(fnol.Text("claimId"), fnol.Text("claimNumber"), exposure.Text("exposure.exposureId"), party);

        // Reserve 1,200.00 within the handler's authority: approved at once.
        (await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 1200m)])).Text("status").ShouldBe("APPROVED");

        // +5,300.00 refers; the handler cannot approve their own request; the manager approves in the PLT inbox.
        var increase = await _money.BuildAndSubmitAsync(claim, [Reserve(claim, 5300m)]);
        increase.Text("status").ShouldBe("PENDING_APPROVAL");
        (await _money.DecideAsync(increase.Text("approvalRequestId"), roles: $"{Handler},{Manager}", user: HandlerUser)).Body.Text("code").ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await _money.DecideAsync(increase.Text("approvalRequestId"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();
        (await _money.SetAsync(increase.Text("setId"))).Text("status").ShouldBe("APPROVED");
        Amount((await _money.FinancialsAsync(claim))["totals"]!["openReserve"]).ShouldBe(6500m);

        // Final payment 6,200.00 to the insured's captured account: refers; the remainder 300.00 is released in the same set.
        var (account, iban) = await _money.CaptureAsync(claim);
        var payment = await _money.BuildAndSubmitAsync(claim, [Payment(claim, 6200m, account)]);
        payment.Text("status").ShouldBe("PENDING_APPROVAL");
        payment.Text("set.transactions.1.reasonCode").ShouldBe("FINAL_RELEASE");
        Amount(payment["set"]!["transactions"]![1]!["amount"]).ShouldBe(-300m);
        var (got, request) = await _money.SendAsync(HttpMethod.Get, $"/api/plt/v1/approval/{payment.Text("approvalRequestId")}", roles: Manager, user: ManagerUser);
        got.StatusCode.ShouldBe(HttpStatusCode.OK);
        request.Text("request.type").ShouldBe("CLM.CLAIM_PAYMENT");
        request.Text("request.authority.type").ShouldBe("CLM.PAYMENT");
        request.Text("request.authority.amount.amount").ShouldBe("6200.00");
        (await _money.DecideAsync(payment.Text("approvalRequestId"))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _money.DrainAsync();

        var paid = (await _money.PaymentsAsync(claim))[0]!;
        paid.Text("status").ShouldBe("CLEARED");
        var paymentId = paid.Text("claimPaymentId");
        (await _money.ScalarAsync<string>($"SELECT state FROM bil.disbursement WHERE source_id = '{paymentId}'")).ShouldBe("CLEARED");
        (await _money.ScalarAsync<string>($"SELECT payload->>'paymentId' FROM plt.outbox_message WHERE event_type = 'PaymentIssued' AND aggregate_id = '{claim.ClaimId}'"))
            .ShouldBe(paymentId);

        var financials = await _money.FinancialsAsync(claim);
        Amount(financials["totals"]!["openReserve"]).ShouldBe(0m);
        Amount(financials["totals"]!["paid"]).ShouldBe(6200m);
        Amount(financials["totals"]!["incurred"]).ShouldBe(6200m);
        Amount(financials["totals"]!["reserved"]).ShouldBe(6200m);

        // The close guard passes; the claim closes.
        var version = int.Parse((await _money.ClaimAsync(claim)).Text("summary.recordVersion"), CultureInfo.InvariantCulture);
        var (closed, close) = await _money.CloseAsync(claim, version);
        closed.StatusCode.ShouldBe(HttpStatusCode.OK, close?.ToJsonString());
        close.Text("claim.status").ShouldBe("CLOSED");

        // REQ-CLM-005: one gap-free per-claim sequence over every CLM event.
        var sequence = await _money.ScalarAsync<string>(
            $"SELECT string_agg(event_type || ':' || aggregate_sequence, ',' ORDER BY aggregate_sequence) FROM plt.outbox_message WHERE aggregate_type = 'Claim' AND aggregate_id = '{claim.ClaimId}'");
        sequence.ShouldBe(
            "ClaimReported:1,CoverageVerified:2,ExposureCreated:3,TransactionSetApproved:4,ReserveChanged:5,TransactionSetApproved:6,ReserveChanged:7,"
            + "TransactionSetApproved:8,ReserveChanged:9,PaymentIssued:10,ClaimClosed:11");
        (await _money.ScalarAsync<string>(
                $"SELECT payload->'delta'->'transaction'->>'amount' FROM plt.outbox_message WHERE event_type = 'ReserveChanged' AND aggregate_id = '{claim.ClaimId}' AND aggregate_sequence = 9"))
            .ShouldBe("-300.00");

        // No IBAN in CLM's tables, events, audit or idempotency store.
        (await _money.ExposuresAsync(iban)).ShouldBe(0);

        // D-ARC-34: connected as the app role, nothing can be appended to the approved payment set or changed in the ledger.
        var setId = payment.Text("setId");
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        var append = $"""
            INSERT INTO clm.financial_transaction (txn_id, set_id, claim_id, reserve_line_id, exposure_id, sequence, txn_number, kind, amount, currency,
                functional_amount, functional_currency, group_amount, group_currency, reason_code, proposed, transaction_date, legal_entity_id, jurisdiction,
                created_at, created_by, record_version)
            SELECT gen_random_uuid(), set_id, claim_id, reserve_line_id, exposure_id, 999, 'X-T0999', 'RESERVE', 10, currency, 10, currency, 10, currency,
                'TAMPER', false, transaction_date, legal_entity_id, jurisdiction, now(), 'tamper', 1
            FROM clm.financial_transaction WHERE set_id = '{setId}' LIMIT 1
            """;
        foreach (var sql in new[]
                 {
                     append,
                     $"UPDATE clm.financial_transaction SET amount = 1 WHERE set_id = '{setId}'",
                     $"UPDATE clm.transaction_set SET status = 'DRAFT' WHERE set_id = '{setId}'",
                     $"UPDATE clm.claim_payment SET amount = 1 WHERE set_id = '{setId}'",
                 })
        {
            await using var command = app.CreateCommand(sql);
            var refused = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
            new[] { PostgresErrorCodes.RestrictViolation, PostgresErrorCodes.InsufficientPrivilege }.ShouldContain(refused.SqlState, sql);
        }

        foreach (var sql in new[] { $"DELETE FROM clm.financial_transaction WHERE set_id = '{setId}'", "TRUNCATE clm.financial_transaction" })
        {
            await using var command = app.CreateCommand(sql);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
        }

        // The owner cannot either: append-only for every role.
        var owner = await Should.ThrowAsync<PostgresException>(() => database.ExecuteAsSuperuserAsync($"UPDATE clm.financial_transaction SET amount = 1 WHERE set_id = '{setId}'", Ct));
        owner.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        Amount((await _money.FinancialsAsync(claim))["totals"]!["incurred"]).ShouldBe(6200m);
    }
}
