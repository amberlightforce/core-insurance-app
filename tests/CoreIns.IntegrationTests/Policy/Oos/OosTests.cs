using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy.Change;
using CoreIns.Modules.Policy.Commands.Change.Oos;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.SharedKernel;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Oos;

/// <summary>
/// SL6-POL-OOS on PostgreSQL 17 (change harness: RAT scripted as a function of engine capacity and value, manual clock): an out-of-sequence change
/// is bound by reverse-and-reapply and the term ends up exactly where the same changes in order would have put it. REQ-POL-006, REQ-POL-033,
/// REQ-POL-108, D-SL6-10. Every amount is ILLUSTRATIVE TEST DATA.
/// </summary>
public sealed class OosTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");

    private ChangeHarness _h = null!;

    public async ValueTask InitializeAsync()
    {
        _h = new ChangeHarness(database);
        await _h.InitializeAsync();
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    /// <summary>An in-sequence change at the clock's day: the vehicle with the given capacity and value, quoted and bound.</summary>
    private async Task<string> ChangeAsync(Issued policy, int capacity, string value)
    {
        var job = await _h.NewChangeAsync(policy);
        await _h.EditVehicleAsync(job, policy, capacity, value);
        (await _h.QuoteAsync(job)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (response, bind) = await _h.BindAsync(job);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        return bind.Text("transactionId");
    }

    private async Task<(string JobId, HttpResponseMessage Response, JsonNode? Body)> StartOosAsync(Issued policy, int day, string? mode = null)
    {
        var (response, body) = await _h.SendAsync(
            HttpMethod.Post, "/api/pol/v1/policy-changes/out-of-sequence",
            new { policyId = policy.PolicyId, effectiveAt = ChangeHarness.Iso(DateTime.SpecifyKind(policy.Start.AddDays(day), DateTimeKind.Utc)), mode });
        return (response.IsSuccessStatusCode ? body.Text("jobId") : string.Empty, response, body);
    }

    private async Task<string> PremiumByChargeTypeAsync(Issued policy) => await _h.ScalarAsync<string>(
        $"SELECT string_agg(charge_type || '=' || total::text, ',' ORDER BY charge_type) FROM (SELECT charge_type, sum(amount) AS total FROM pol.charge_line WHERE policy_id = '{policy.PolicyId}' AND charge_category = 'PREMIUM' GROUP BY charge_type) t");

    private async Task<string> RiskByPeriodAsync(Issued policy) => await _h.ScalarAsync<string>(
        $"SELECT string_agg((snapshot->'vehicles'->0->>'engineCapacityCc') || '/' || (snapshot->'vehicles'->0->'value'->>'amount'), ' ; ' ORDER BY valid_from) FROM pol.segment WHERE policy_id = '{policy.PolicyId}' AND recorded_to IS NULL");

    [Fact]
    public async Task REQ_POL_006_033_three_sequential_changes_with_an_earlier_one_inserted_equal_the_in_order_result()
    {
        // Reference: the same three changes bound in effective order.
        var inOrder = await _h.IssueAsync();
        _h.SetDay(inOrder, 5);
        await ChangeAsync(inOrder, 1600, "15000.00");
        _h.SetDay(inOrder, 10);
        await ChangeAsync(inOrder, 1600, "16000.00");
        _h.SetDay(inOrder, 20);
        await ChangeAsync(inOrder, 1600, "17000.00");

        // Under test: day 10 and day 20 first, then day 5 inserted at day 25.
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 10);
        var first = await ChangeAsync(policy, 1400, "16000.00");
        _h.SetDay(policy, 20);
        var second = await ChangeAsync(policy, 1400, "17000.00");
        _h.SetDay(policy, 25);
        var before = await _h.ScalarAsync<decimal>($"SELECT sum(amount) FROM pol.charge_line WHERE policy_id = '{policy.PolicyId}' AND charge_category = 'PREMIUM'");

        // The plain create still refuses it (D-SL3-02); the out-of-sequence operation starts it.
        var plain = await _h.StartChangeAsync(policy, ChangeHarness.Iso(DateTime.SpecifyKind(policy.Start.AddDays(5), DateTimeKind.Utc)));
        plain.Body.Text("code").ShouldBe("POL-ERR-OUT-OF-SEQUENCE");

        var (job, started, startBody) = await StartOosAsync(policy, 5);
        started.StatusCode.ShouldBe(HttpStatusCode.Created, startBody?.ToJsonString());
        await _h.EditVehicleAsync(job, policy, 1600, "15000.00");
        var (quoted, quote) = await _h.QuoteAsync(job);
        quoted.StatusCode.ShouldBe(HttpStatusCode.OK, quote?.ToJsonString());
        var (previewed, preview) = await _h.PreviewAsync(job);
        previewed.StatusCode.ShouldBe(HttpStatusCode.OK, preview?.ToJsonString());
        preview.Text("servicingPreview.totalChange.amount").ShouldBe(quote.Text("servicingPreview.totalChange.amount"));

        var (bound, bind) = await _h.BindAsync(job);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        var oosTransaction = bind.Text("transactionId");

        // Final premiums and risk per period equal the in-order result.
        (await PremiumByChargeTypeAsync(policy)).ShouldBe(await PremiumByChargeTypeAsync(inOrder));
        (await RiskByPeriodAsync(policy)).ShouldBe(await RiskByPeriodAsync(inOrder));
        (await RiskByPeriodAsync(policy)).ShouldBe("1400/15000.00 ; 1600/15000.00 ; 1600/16000.00 ; 1600/17000.00");

        // The delta set (one complete set, NET) sums to the premium difference and points at what it corrects.
        var after = await _h.ScalarAsync<decimal>($"SELECT sum(amount) FROM pol.charge_line WHERE policy_id = '{policy.PolicyId}' AND charge_category = 'PREMIUM'");
        var set = await _h.ScalarAsync<decimal>($"SELECT sum(amount) FROM pol.charge_line WHERE transaction_id = '{oosTransaction}' AND charge_category = 'PREMIUM'");
        set.ShouldBe(after - before);
        set.ShouldNotBe(0m);
        (await _h.ScalarAsync<long>(
            $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND set_id = '{oosTransaction}' AND payload->>'correctionOf' = '{first}' AND payload->>'deltaKind' = 'NET'"))
            .ShouldBe(await _h.ScalarAsync<long>($"SELECT count(*) FROM pol.charge_line WHERE transaction_id = '{oosTransaction}'"));

        // One PolicyChanged for the whole correction (the CLM re-verification fires once per claim, not per segment); a reversal and a re-application per later change.
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyChanged' AND business_keys->>'transactionId' = '{oosTransaction}'")).ShouldBe(1);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'TransactionReversed' AND business_keys->>'reversalTransactionId' = '{oosTransaction}' AND set_size = 2")).ShouldBe(2);
        (await _h.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'TransactionReapplied' AND business_keys->>'newTransactionId' = '{oosTransaction}' AND set_size = 2")).ShouldBe(2);
        (await _h.ScalarAsync<string>($"SELECT original_refs::text FROM pol.oos_correction WHERE transaction_id = '{oosTransaction}'")).ShouldContain(first);
        (await _h.ScalarAsync<string>($"SELECT original_refs::text FROM pol.oos_correction WHERE transaction_id = '{oosTransaction}'")).ShouldContain(second);
        (await _h.ScalarAsync<string>($"SELECT mode || '/' || status FROM pol.oos_correction WHERE transaction_id = '{oosTransaction}'")).ShouldBe("NET/APPLIED");

        // The term replays after the correction: an ordinary change on top equals the same change on the in-order policy.
        _h.SetDay(policy, 30);
        await ChangeAsync(policy, 1800, "17000.00");
        _h.SetDay(inOrder, 30);
        await ChangeAsync(inOrder, 1800, "17000.00");
        (await PremiumByChargeTypeAsync(policy)).ShouldBe(await PremiumByChargeTypeAsync(inOrder));
    }

    [Fact]
    public async Task D_SL6_10_gross_mode_is_not_enabled_through_the_api()
    {
        var policy = await _h.IssueAsync();
        _h.SetDay(policy, 10);
        var (_, response, body) = await StartOosAsync(policy, 5, "GROSS");
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
        body.Text("code").ShouldBe("POL-ERR-VALIDATION");
    }

    // ---- pure parts: no database ----------------------------------------------------------------------------------------------

    private static Instant At(int y, int m, int d) => Instant.FromUtcDateTime(TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Unspecified), Athens));

    private static readonly ServicingTerm Term = new(At(2026, 1, 1), At(2027, 1, 1), Currency.EUR, DayCountConvention.Act365F, Athens);

    private static ChargeRate Rate(decimal annual, string type = "PREM-MTPL") => new("VEH-1", "MTPL", type, "PREMIUM", annual);

    private static ServicingEngine Engine() => new(new ReferenceProration(), a => decimal.Round(a, 2, MidpointRounding.AwayFromZero));

    private static ServicingState Open(params ChargeRate[] rates) => Engine().Apply(null, new NewTermIntent(Term, rates), new DeltaCorrelation("c", "s")).State!;

    [Fact]
    public void Net_lines_sum_to_the_premium_difference_and_the_chain_equals_the_in_order_history()
    {
        var corr = new DeltaCorrelation("c", "s");
        var opened = Open(Rate(300m));
        var written = OosEngine.ApplyChain(Engine(), opened, [new OosStep(At(2026, 4, 1), [Rate(330m)]), new OosStep(At(2026, 8, 1), [Rate(345m)])], corr).State!;

        // Insert a change at 2026-02-01 (+20 on the rate); the later two carry it.
        var corrected = OosEngine.ApplyChain(
            Engine(), opened, [new OosStep(At(2026, 2, 1), [Rate(320m)]), new OosStep(At(2026, 4, 1), [Rate(350m)]), new OosStep(At(2026, 8, 1), [Rate(365m)])], corr).State!;

        var lines = OosEngine.Lines(OosMode.Net, written, corrected);
        lines.Sum(l => l.Amount).ShouldBe(corrected.Segments.Sum(s => s.Amount) - written.Segments.Sum(s => s.Amount));
        lines.ShouldAllBe(l => l.Role == "NET");
        (OosEngine.Cumulative(written).Values.Single() + lines.Sum(l => l.Amount)).ShouldBe(OosEngine.Cumulative(corrected).Values.Single());
        OosEngine.NetDeltas(lines, Term, At(2026, 2, 1), corr).ShouldAllBe(d => d.TransactionKind == TransactionKind.EndorsementDebit && d.ValidFrom == At(2026, 2, 1));
    }

    [Fact]
    public void Gross_mode_reverses_the_written_cumulative_and_reapplies_the_new_one()
    {
        var corr = new DeltaCorrelation("c", "s");
        var opened = Open(Rate(300m));
        var written = OosEngine.ApplyChain(Engine(), opened, [new OosStep(At(2026, 4, 1), [Rate(330m)])], corr).State!;
        var corrected = OosEngine.ApplyChain(Engine(), opened, [new OosStep(At(2026, 2, 1), [Rate(320m)]), new OosStep(At(2026, 4, 1), [Rate(350m)])], corr).State!;

        var net = OosEngine.Lines(OosMode.Net, written, corrected);
        var gross = OosEngine.Lines(OosMode.Gross, written, corrected);

        gross.Select(l => l.Role).ShouldBe(["REVERSAL", "REAPPLY"]);
        gross[0].Amount.ShouldBe(-written.Segments.Sum(s => s.Amount));
        gross.Sum(l => l.Amount).ShouldBe(net.Sum(l => l.Amount));
    }

    [Fact]
    public void An_unchanged_history_has_no_lines()
    {
        var corr = new DeltaCorrelation("c", "s");
        var opened = Open(Rate(300m), Rate(40m, "PREM-OD"));
        var state = OosEngine.ApplyChain(Engine(), opened, [new OosStep(At(2026, 4, 1), [Rate(330m), Rate(40m, "PREM-OD")])], corr).State!;

        OosEngine.Lines(OosMode.Net, state, state).ShouldBeEmpty();
    }

    private sealed class LockedMonths(params (int Year, int Month)[] months) : IOosFinancePeriods
    {
        public Task<bool> IsLockedAsync(BusinessDate date, CancellationToken cancellationToken) =>
            Task.FromResult(months.Contains((date.Value.Year, date.Value.Month)));
    }

    [Fact]
    public async Task REQ_POL_108_a_locked_finance_month_moves_the_booking_to_the_first_open_one()
    {
        var applier = new OosApplier(null!, null!, new LockedMonths((2026, 10), (2026, 11)));

        var moved = await applier.BookingDateAsync(new BusinessDate(new DateOnly(2026, 10, 19)), TestContext.Current.CancellationToken);
        moved.Value.ShouldBe((new BusinessDate(new DateOnly(2026, 12, 1)), true));

        var open = await new OosApplier(null!, null!, new UnlockedFinancePeriods()).BookingDateAsync(new BusinessDate(new DateOnly(2026, 10, 19)), TestContext.Current.CancellationToken);
        open.Value.ShouldBe((new BusinessDate(new DateOnly(2026, 10, 19)), false));
    }
}
