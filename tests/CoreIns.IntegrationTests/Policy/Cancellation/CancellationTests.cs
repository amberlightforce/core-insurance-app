using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Cancellation;

/// <summary>
/// SL3-POL-CANCEL over HTTP on PostgreSQL 17: policyholder cancellation now and flat cancellation (E2E-03 steps 1-3). The clock is
/// manual: bind at day 0, cancel at day 120. Every amount is ILLUSTRATIVE TEST DATA; the IPT treatment is provisional
/// (KEEP_NOT_REDUCED, PendingOpinion, D-SL3-05..08). REQ ids are in the test names.
/// </summary>
public sealed class CancellationTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private CancellationSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new CancellationSlice(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Policy:LockWaitSeconds"] = "1" });
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task E2E_03_REQ_POL_205_208_214_215_216_day_120_credit_is_288_63_and_ipt_is_kept()
    {
        var policy = await _slice.BindAsync();
        policy.TermState.ShouldBe("IN_FORCE");
        await _slice.AdvanceAsync(TimeSpan.FromDays(120));

        var (response, body) = await _slice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());

        // REQ-POL-206/208: ProRata (illustrative), effective = request time; REQ-POL-004: the job went Draft -> Quoted -> Bound.
        body.Text("state").ShouldBe("BOUND");
        body.Text("kind").ShouldBe("STANDARD");
        body.Text("effectiveAt").ShouldBe(_slice.Clock.Now.ToString());
        var preview = body!["servicingPreview"]!;
        preview.Text("refundMethod").ShouldBe("PRO_RATA");
        preview.Text("cancellationSource").ShouldBe("Policyholder");
        preview.Text("transactionKind").ShouldBe("CANCELLATION");

        // REQ-POL-207/214: the preview per element x charge type: 430.00 x 245/365 = 288.63 credited; the IPT line is kept.
        preview.Text("annualBefore.amount").ShouldBe("430.00");
        preview.Text("premiumChange.amount").ShouldBe("-288.63");
        preview.Text("taxChange.amount").ShouldBe("0.00");
        preview.Text("totalChange.amount").ShouldBe("-288.63");
        preview.Text("refundDue.amount").ShouldBe("288.63");
        preview.Text("additionalDue.amount").ShouldBe("0.00");
        preview["provisional"]!.GetValue<bool>().ShouldBeTrue();
        var prorated = preview["proratedLines"]!.AsArray().Single()!;
        prorated.Text("chargeType").ShouldBe("PREM-MTPL");
        prorated.Text("amount.amount").ShouldBe("-288.63");
        prorated.Text("annualAmount.amount").ShouldBe("430.00");
        prorated["days"]!.GetValue<int>().ShouldBe(245);
        prorated["termDays"]!.GetValue<int>().ShouldBe(365);
        var iptLine = preview["taxLines"]!.AsArray().Single()!;
        iptLine.Text("chargeType").ShouldBe("GR-IPT");
        iptLine.Text("amount.amount").ShouldBe("0.00");
        iptLine.Text("treatmentAction").ShouldBe("KEEP_NOT_REDUCED");
        iptLine.Text("legalStatus").ShouldBe("PendingOpinion");
        iptLine["provisional"]!.GetValue<bool>().ShouldBeTrue();
        iptLine.Text("ruleId").ShouldBe("GR-TRT-IPT-CANCEL-POLICYHOLDER");

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var transactionId = await ScalarAsync<Guid>(data, $"SELECT bound_transaction_id FROM pol.job WHERE job_id = '{body.Text("jobId")}'");
        var termId = policy.TermId;

        // REQ-POL-119/122/214: NET deltas, one per element x charge type, kind CANCELLATION, source recorded, one record time.
        var deltas = await RowsAsync(data, $"SELECT charge_type, amount, transaction_kind, cancellation_source, treatment_rule_id, provisional, legal_status, set_index, set_size, charge_category FROM pol.charge_line WHERE transaction_id = '{transactionId}' ORDER BY set_index");
        deltas.Count.ShouldBe(2);
        deltas[0][0].ShouldBe("PREM-MTPL");
        deltas[0][1].ShouldBe(-288.63m);
        deltas[0][2].ShouldBe("CANCELLATION");
        deltas[0][3].ShouldBe("Policyholder");
        deltas[1][0].ShouldBe("GR-IPT");
        deltas[1][1].ShouldBe(0m);
        deltas[1][2].ShouldBe("CANCELLATION");
        deltas[1][4].ShouldBe("GR-TRT-IPT-CANCEL-POLICYHOLDER");
        deltas[1][5].ShouldBe(true);
        deltas[1][6].ShouldBe("PendingOpinion");
        deltas[1][8].ShouldBe(2);

        // Σ deltas of the term = written - earned (430.00 - 288.63 = 141.37 earned premium); IPT stays 64.50.
        (await ScalarAsync<decimal>(data, $"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{termId}' AND charge_category = 'PREMIUM'")).ShouldBe(141.37m);
        (await ScalarAsync<decimal>(data, $"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{termId}' AND charge_category = 'TAX'")).ShouldBe(64.50m);
        (await ScalarAsync<decimal>(data, $"SELECT premium FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe(-288.63m);
        (await ScalarAsync<string>(data, $"SELECT kind FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe("CANCELLATION");

        // Every row of the cancellation carries the one record time t = the policy watermark (D-SL3-03).
        var watermark = await ScalarAsync<DateTime>(data, $"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'");
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.charge_line WHERE transaction_id = '{transactionId}' AND recorded_at = '{watermark:O}'")).ShouldBe(2);
        (await ScalarAsync<DateTime>(data, $"SELECT recorded_at FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe(watermark);

        // The term: previous version closed at t, new version Cancelled with cancelled_at = the effective time.
        var versions = await RowsAsync(data, $"SELECT state, recorded_from, recorded_to, cancelled_at, head_transaction_id::text FROM pol.policy_term WHERE term_id = '{termId}' ORDER BY recorded_from");
        versions.Count.ShouldBe(2);
        versions[0][0].ShouldBe("IN_FORCE");
        versions[0][2].ShouldBe(watermark);
        versions[1][0].ShouldBe("CANCELLED");
        versions[1][1].ShouldBe(watermark);
        versions[1][2].ShouldBe(DBNull.Value);
        ((DateTime)versions[1][3]).ShouldBe(_slice.Clock.Now.ToUtcDateTime());
        versions[1][4].ShouldBe(transactionId.ToString());

        // The cover segment now ends at the effective time; the superseded one is closed, not deleted (REQ-POL-078).
        var segments = await RowsAsync(data, $"SELECT valid_to, recorded_to FROM pol.segment WHERE term_id = '{termId}' ORDER BY recorded_from");
        segments.Count.ShouldBe(2);
        segments[0][1].ShouldBe(watermark);
        ((DateTime)segments[1][0]).ShouldBe(_slice.Clock.Now.ToUtcDateTime());
        segments[1][1].ShouldBe(DBNull.Value);

        // REQ-POL-216 / REQ-POL-005: PolicyCancelled with source and method, and one complete ChargeDeltaEmitted set, in the same transaction.
        (await ScalarAsync<string>(data, $"SELECT payload->>'source' || '/' || (payload->>'refundMethod') || '/' || (payload->>'effectiveDate') FROM plt.outbox_message WHERE event_type = 'PolicyCancelled' AND aggregate_id = '{policy.PolicyId}'"))
            .ShouldBe($"Policyholder/PRO_RATA/{_slice.Clock.Now.ToBusinessDate(TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens"))}");
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND set_id = '{transactionId}' AND set_size = 2")).ShouldBe(2);
        (await ScalarAsync<string>(data, $"SELECT payload->>'taxTreatmentRef' FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND set_id = '{transactionId}' AND payload->>'chargeType' = 'GR-IPT'"))
            .ShouldBe("GR-TRT-IPT-CANCEL-POLICYHOLDER/0.1.0/KEEP_NOT_REDUCED");

        // The job carries the cancellation facts (REQ-POL-004).
        var job = await RowsAsync(data, $"SELECT job_type, state, cancellation_source, cancellation_kind, refund_method, reason_code, target_term_id::text FROM pol.job WHERE job_id = '{body.Text("jobId")}'");
        job.Single().ShouldBe(["CANCELLATION", "BOUND", "Policyholder", "STANDARD", "PRO_RATA", "CUSTOMER_REQUEST", termId]);

        // pol.Policy.get shows Cancelled from now on, and the old record still answers "in force" as known before the cancellation.
        var (current, now) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/{policy.PolicyId}", roles: Billing);
        current.StatusCode.ShouldBe(HttpStatusCode.OK, now?.ToJsonString());
        now.Text("policy.status").ShouldBe("CANCELLED");
        var knownBefore = _slice.Clock.Now.Minus(TimeSpan.FromDays(60)).ToString();
        var (past, then) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/{policy.PolicyId}?validAt={Uri.EscapeDataString(knownBefore)}&knownAt={Uri.EscapeDataString(knownBefore)}", roles: Billing);
        past.StatusCode.ShouldBe(HttpStatusCode.OK, then?.ToJsonString());
        then.Text("policy.status").ShouldBe("IN_FORCE");

        // The treatment was asked once for the IPT line, with the source (single call, REQ-MKT-332).
        var asked = _slice.Tax.Requests.Single();
        asked.CancellationSource.ShouldBe("Policyholder");
        asked.ChargeType.ShouldBe("GR-IPT");
    }

    [Fact]
    public async Task REQ_POL_217_flat_cancellation_of_a_scheduled_term_credits_exactly_the_written_amount()
    {
        var policy = await _slice.BindAsync(effectiveIn: TimeSpan.FromDays(10));
        policy.TermState.ShouldBe("SCHEDULED");

        var (response, body) = await _slice.CancelAsync(policy.PolicyId, kind: "Flat");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("kind").ShouldBe("FLAT");
        body!["servicingPreview"]!.Text("refundMethod").ShouldBe("FULL_REFUND");
        body.Text("effectiveAt").ShouldBe(_slice.Clock.Now.Plus(TimeSpan.FromDays(10)).ToString());
        body["servicingPreview"]!.Text("premiumChange.amount").ShouldBe("-430.00");
        body["servicingPreview"]!.Text("refundDue.amount").ShouldBe("430.00");

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<decimal>(data, $"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{policy.TermId}' AND charge_category = 'PREMIUM'")).ShouldBe(0m);
        (await ScalarAsync<decimal>(data, $"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{policy.TermId}' AND charge_category = 'TAX'")).ShouldBe(64.50m);
        // The cover never started: no segment is left for any valid time (the superseded one is closed, none is re-recorded).
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.segment WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL")).ShouldBe(0);

        // A flat cancellation is only for a Scheduled term (REQ-POL-217).
        var started = await _slice.BindAsync();
        var (refused, problem) = await _slice.CancelAsync(started.PolicyId, kind: "Flat");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
    }

    [Theory]
    [InlineData("Insurer")]
    [InlineData("NonPayment")]
    [InlineData("DistanceWithdrawal")]
    [InlineData("Statutory")]
    public async Task REQ_POL_205_209_other_sources_fail_closed(string source)
    {
        var policy = await _slice.BindAsync();
        var (response, problem) = await _slice.CancelAsync(policy.PolicyId, source);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-VALIDATION");
        problem!.ToJsonString().ShouldContain("not available in this release");
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_POL_205_an_unknown_source_is_a_validation_error()
    {
        var policy = await _slice.BindAsync();
        var (response, problem) = await _slice.CancelAsync(policy.PolicyId, "Nobody");
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-VALIDATION");
    }

    [Fact]
    public async Task REQ_POL_209_a_future_effective_date_is_refused_as_scheduled_cancellation_is_not_available()
    {
        var policy = await _slice.BindAsync();
        var future = _slice.Clock.Now.Plus(TimeSpan.FromDays(5)).ToString();
        var (response, problem) = await _slice.CancelAsync(policy.PolicyId, effectiveAt: future);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-EFFDATE-LIMIT");
        problem!.ToJsonString().ShouldContain("Scheduled cancellation is not available in this release");
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_POL_208_backdating_is_refused_and_an_effective_time_equal_to_now_is_accepted()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(3));
        var past = _slice.Clock.Now.Minus(TimeSpan.FromDays(1)).ToString();
        var (backdated, problem) = await _slice.CancelAsync(policy.PolicyId, effectiveAt: past);
        backdated.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-EFFDATE-LIMIT");

        var (now, body) = await _slice.CancelAsync(policy.PolicyId, effectiveAt: _slice.Clock.Now.Minus(TimeSpan.FromHours(1)).ToString());
        now.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["servicingPreview"]!.Text("premiumChange.amount").ShouldBe("-426.47");
    }

    [Fact]
    public async Task REQ_POL_004_a_second_cancellation_is_an_illegal_transition()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(30));
        (await _slice.CancelAsync(policy.PolicyId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _slice.AdvanceAsync(TimeSpan.FromDays(1));
        var (second, problem) = await _slice.CancelAsync(policy.PolicyId);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}' AND kind = 'CANCELLATION'")).ShouldBe(1);
    }

    [Fact]
    public async Task D_SL3_21_a_term_with_a_scheduled_successor_cannot_be_cancelled()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(30));
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using (var command = data.CreateCommand($"""
            UPDATE pol.policy SET last_recorded_at = last_recorded_at + interval '1 microsecond', record_version = record_version + 1 WHERE policy_id = '{policy.PolicyId}';
            CREATE TEMP TABLE successor AS SELECT * FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL;
            UPDATE successor SET term_version_id = gen_random_uuid(), term_id = gen_random_uuid(), term_number = 2, valid_from = valid_to,
                valid_to = valid_to + interval '1 year', state = 'SCHEDULED', predecessor_term_id = '{policy.TermId}',
                recorded_from = (SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}');
            INSERT INTO pol.policy_term SELECT * FROM successor;
            """))
        {
            await command.ExecuteNonQueryAsync(Ct);
        }

        var (response, problem) = await _slice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
        problem!.ToJsonString().ShouldContain("cancel the renewal term first");

        await AssertUntouchedAsync(policy);
    }

    [Fact]
    public async Task D_SL3_21_a_term_with_an_open_renewal_job_cannot_be_cancelled()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(30));
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using (var command = data.CreateCommand($"""
            CREATE TEMP TABLE renewal AS SELECT * FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'SUBMISSION';
            UPDATE renewal SET job_id = gen_random_uuid(), job_number = 'Q' || lpad((random() * 999999999)::int::text, 9, '0'),
                job_type = 'RENEWAL', state = 'QUOTED', expiring_term_id = '{policy.TermId}', bound_transaction_id = NULL;
            INSERT INTO pol.job SELECT * FROM renewal;
            """))
        {
            await command.ExecuteNonQueryAsync(Ct);
        }

        var (response, problem) = await _slice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
        await AssertUntouchedAsync(policy);
    }

    [Fact]
    public async Task D1_the_day_count_comes_from_the_pinned_artefact_ACT_365F_on_a_leap_term()
    {
        // MOTOR-GR 1.0 declares ACT/365F. Bound on 2027-10-08 the term runs 366 days (Feb 29 2028); at day 100 ACT/365F has earned 100/365,
        // so 430.00 x 265/365 = 312.19 is credited (TERM_RATIO would credit 430.00 x 266/366 = 312.51).
        await _slice.AdvanceAsync(TimeSpan.FromDays(365));
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(100));
        var (response, body) = await _slice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["servicingPreview"]!.Text("premiumChange.amount").ShouldBe("-312.19");
        body["servicingPreview"]!.Text("proratedLines.0.termDays").ShouldBe("366");
    }

    [Fact]
    public async Task D2_a_flat_non_refundable_fee_is_kept_and_only_the_prorated_premium_is_credited()
    {
        await using var feeSlice = new CancellationSlice(database.AppConnectionString, fee: 20.00m, settings: new Dictionary<string, string?> { ["Policy:LockWaitSeconds"] = "1" });
        await feeSlice.SeedAsync();
        var policy = await feeSlice.BindAsync();
        await feeSlice.AdvanceAsync(TimeSpan.FromDays(120));
        var (response, body) = await feeSlice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var preview = body!["servicingPreview"]!;
        preview.Text("premiumChange.amount").ShouldBe("-288.63");
        preview["proratedLines"]!.AsArray().Select(l => l!.Text("chargeType")).ShouldBe(["PREM-MTPL"]);

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<decimal>(data, $"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{policy.TermId}' AND charge_type = 'FEE-POLICY'")).ShouldBe(20.00m);

        // Flat cancellation of a Scheduled term: the premium is credited in full, the non-refundable fee stays.
        var scheduled = await feeSlice.BindAsync(effectiveIn: TimeSpan.FromDays(10));
        var (flat, flatBody) = await feeSlice.CancelAsync(scheduled.PolicyId, kind: "Flat");
        flat.StatusCode.ShouldBe(HttpStatusCode.OK, flatBody?.ToJsonString());
        flatBody!["servicingPreview"]!.Text("premiumChange.amount").ShouldBe("-430.00");
        (await ScalarAsync<decimal>(data, $"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{scheduled.TermId}' AND charge_type = 'FEE-POLICY'")).ShouldBe(20.00m);
    }

    [Fact]
    public async Task D6_a_cancellation_on_the_last_athens_date_of_the_term_credits_nothing_and_writes_no_tax_line()
    {
        var policy = await _slice.BindAsync();
        // The term ends 2027-10-08 12:00 Athens; 10:00 the same day is its last Athens date: no whole day remains.
        await _slice.AdvanceAsync(TimeSpan.FromDays(365) - TimeSpan.FromHours(2));
        var (response, body) = await _slice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["servicingPreview"]!.Text("premiumChange.amount").ShouldBe("0.00");
        body["servicingPreview"]!.Text("refundDue.amount").ShouldBe("0.00");
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.charge_line WHERE policy_id = '{policy.PolicyId}' AND transaction_kind = 'CANCELLATION'")).ShouldBe(0);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL AND state = 'CANCELLED'")).ShouldBe(1);
    }

    [Fact]
    public async Task D3_a_valid_time_before_the_effective_time_known_after_it_is_still_in_force()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(120));
        (await _slice.CancelAsync(policy.PolicyId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = _slice.Clock.Now.Minus(TimeSpan.FromDays(60)).ToString();
        var (read, body) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/{policy.PolicyId}?validAt={Uri.EscapeDataString(before)}", roles: Billing);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("policy.status").ShouldBe("IN_FORCE");
        var (now, current) = await SendAsync(_slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/{policy.PolicyId}", roles: Billing);
        now.StatusCode.ShouldBe(HttpStatusCode.OK);
        current.Text("policy.status").ShouldBe("CANCELLED");
    }

    [Fact]
    public async Task REQ_POL_209_an_effective_time_hours_ahead_on_the_same_day_is_refused()
    {
        var policy = await _slice.BindAsync();
        var (response, problem) = await _slice.CancelAsync(policy.PolicyId, effectiveAt: _slice.Clock.Now.Plus(TimeSpan.FromHours(2)).ToString());
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-EFFDATE-LIMIT");
        var (ok, body) = await _slice.CancelAsync(policy.PolicyId, effectiveAt: _slice.Clock.Now.Plus(TimeSpan.FromMinutes(3)).ToString());
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

    [Fact]
    public async Task REQ_POL_209_cancel_after_expiry_is_refused()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(370));
        var (response, problem) = await _slice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");
    }

    [Fact]
    public async Task REQ_POL_071_the_dry_run_equals_the_real_run_and_writes_nothing()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(120));
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var watermark = await ScalarAsync<DateTime>(data, $"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'");
        var outbox = await ScalarAsync<long>(data, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{policy.PolicyId}'");

        var (dry, preview) = await _slice.CancelAsync(policy.PolicyId, dryRun: true);
        dry.StatusCode.ShouldBe(HttpStatusCode.OK, preview?.ToJsonString());
        preview!["servicingPreview"]!.Text("premiumChange.amount").ShouldBe("-288.63");

        (await ScalarAsync<DateTime>(data, $"SELECT last_recorded_at FROM pol.policy WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(watermark);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(0);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_term WHERE term_id = '{policy.TermId}'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.charge_line WHERE policy_id = '{policy.PolicyId}' AND transaction_kind = 'CANCELLATION'")).ShouldBe(0);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{policy.PolicyId}'")).ShouldBe(outbox);

        var (real, done) = await _slice.CancelAsync(policy.PolicyId);
        real.StatusCode.ShouldBe(HttpStatusCode.OK, done?.ToJsonString());
        // Dry run equals real: the same preview, line by line (the dry run's job and transaction ids are rolled back).
        done!["servicingPreview"]!.ToJsonString().ShouldBe(preview["servicingPreview"]!.ToJsonString());
        done.Text("effectiveAt").ShouldBe(preview.Text("effectiveAt"));
    }

    [Fact]
    public async Task Pitfall_idempotent_replay_returns_the_same_cancellation_and_writes_once()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(10));
        var key = Guid.NewGuid();
        var (first, one) = await _slice.CancelAsync(policy.PolicyId, key: key);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, one?.ToJsonString());
        var (replay, two) = await _slice.CancelAsync(policy.PolicyId, key: key);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, two?.ToJsonString());
        two.Text("jobId").ShouldBe(one.Text("jobId"));
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}' AND kind = 'CANCELLATION'")).ShouldBe(1);
    }

    [Fact]
    public async Task Pitfall_15_a_writer_holding_the_policy_lock_makes_the_cancel_a_409_not_a_500_and_it_succeeds_afterwards()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(10));
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using (var holder = await data.OpenConnectionAsync(Ct))
        {
            await using var transaction = await holder.BeginTransactionAsync(Ct);
            await using (var lockRow = new NpgsqlCommand($"SELECT 1 FROM pol.policy WHERE policy_id = '{policy.PolicyId}' FOR UPDATE", holder, transaction))
            {
                await lockRow.ExecuteScalarAsync(Ct);
            }

            var (blocked, problem) = await _slice.CancelAsync(policy.PolicyId);
            blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict, problem?.ToJsonString());
            problem.Text("code").ShouldBe("POL-ERR-STALE");
            await transaction.RollbackAsync(Ct);
        }

        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);
        var (after, body) = await _slice.CancelAsync(policy.PolicyId);
        after.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
    }

    [Fact]
    public async Task Pitfall_15_two_concurrent_cancellations_one_wins_and_the_other_gets_a_409()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(20));
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => _slice.CancelAsync(policy.PolicyId)));
        results.ShouldAllBe(r => (int)r.Response.StatusCode < 500, "a lost race is a 409, never a 500 (PITFALLS 15)");
        var codes = results.Select(r => r.Response.StatusCode).Order().ToList();
        codes.ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        results.Single(r => r.Response.StatusCode == HttpStatusCode.Conflict).Body.Text("code").ShouldBeOneOf("POL-ERR-ILLEGAL-TRANSITION", "POL-ERR-STALE");
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}' AND kind = 'CANCELLATION'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ_POL_215_a_missing_treatment_rule_fails_the_cancellation_closed_and_writes_nothing()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(30));
        _slice.Tax.Behaviour = FakeTaxCalculator.Mode.RuleMissing;
        var (response, problem) = await _slice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-GATE-FAILED");
        await AssertUntouchedAsync(policy);
    }

    [Fact]
    public async Task REQ_POL_215_production_refuses_the_provisional_treatment_and_nothing_is_written()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(30));
        _slice.Tax.Behaviour = FakeTaxCalculator.Mode.NotSettledInProduction;
        var (response, problem) = await _slice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("MKT-ERR-CFG-NOT-SETTLED");
        await AssertUntouchedAsync(policy);
    }

    [Fact]
    public async Task REQ_POL_215_only_keep_not_reduced_is_implemented_other_treatments_fail_closed()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(30));
        _slice.Tax.Behaviour = FakeTaxCalculator.Mode.ApplyAction;
        var (response, problem) = await _slice.CancelAsync(policy.PolicyId);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-GATE-FAILED");
        await AssertUntouchedAsync(policy);
    }

    [Fact]
    public async Task Permissions_the_operation_needs_a_granted_role()
    {
        var policy = await _slice.BindAsync();
        var (denied, _) = await _slice.CancelAsync(policy.PolicyId, roles: Billing);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var anonymous = new HttpRequestMessage(HttpMethod.Post, "/api/pol/v1/cancellations");
        var unauthenticated = await _slice.Client.SendAsync(anonymous, Ct);
        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Another_legal_entity_or_unknown_policy_is_not_found()
    {
        var (response, problem) = await _slice.CancelAsync(Guid.NewGuid().ToString());
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-NOT-FOUND");
    }

    private async Task AssertUntouchedAsync(BoundPolicy policy)
    {
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_transaction WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL AND state = 'IN_FORCE'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(0);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{policy.PolicyId}' AND event_type = 'PolicyCancelled'")).ShouldBe(0);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<object[]>> RowsAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object[]>();
        while (await reader.ReadAsync(Ct))
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }
}
