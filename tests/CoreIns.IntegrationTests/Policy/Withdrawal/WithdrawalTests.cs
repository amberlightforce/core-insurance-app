using System.Net;
using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Policy.Cancellation;
using CoreIns.SharedKernel;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Withdrawal;

/// <summary>
/// SL6-POL-WITHDRAW over HTTP on PostgreSQL 17: <c>pol.Withdrawal.submit</c> voids the term ab initio with a full refund, exactly
/// once. Amounts are ILLUSTRATIVE TEST DATA (430.00 premium, 64.50 IPT); the window is the illustrative 14 days (D-SL6-06).
/// </summary>
public sealed class WithdrawalTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private CancellationSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new CancellationSlice(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Policy:LockWaitSeconds"] = "1" });
        await _slice.SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private Task<(HttpResponseMessage Response, JsonNode? Body)> SubmitAsync(
        string policyId, Instant receivedAt, string channelRef, string right = "WITHDRAWAL", bool dryRun = false) =>
        SendAsync(
            _slice.Client, HttpMethod.Post, "/api/pol/v1/withdrawals/submit" + (dryRun ? "?dryRun=true" : string.Empty),
            new { policyId, right, receivedAt = receivedAt.ToString(), channelRef }, roles: Underwriter, key: Guid.NewGuid());

    [Fact]
    public async Task Withdrawal_voids_the_term_with_full_refund_incl_ipt_and_zero_deduction_exactly_once()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(3));
        var received = _slice.Clock.Now;

        var (response, body) = await SubmitAsync(policy.PolicyId, received, "CHN-WD-1");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["replayed"]!.GetValue<bool>().ShouldBeFalse();
        body.Text("voidJob.state").ShouldBe("BOUND");
        body.Text("voidJob.right").ShouldBe("WITHDRAWAL");
        body.Text("voidJob.refundDue.amount").ShouldBe("494.50");
        body.Text("voidJob.costOfCoverDeduction.amount").ShouldBe("0.00");
        var preview = body["refundPreview"]!;
        preview.Text("transactionKind").ShouldBe("DISTANCE_WITHDRAWAL_VOID");
        preview.Text("cancellationSource").ShouldBe("DistanceWithdrawal");
        preview.Text("refundMethod").ShouldBe("FULL_REFUND");
        preview.Text("premiumChange.amount").ShouldBe("-430.00");
        preview.Text("taxChange.amount").ShouldBe("-64.50");
        preview.Text("refundDue.amount").ShouldBe("494.50");
        var ipt = preview["taxLines"]!.AsArray().Single()!;
        ipt.Text("treatmentAction").ShouldBe("REVERSE_AS_VOID");
        ipt.Text("amount.amount").ShouldBe("-64.50");

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var jobId = body.Text("voidJob.jobId");
        var transactionId = body.Text("voidJob.transactionId");

        // The term is Voided from its cover start; the transaction is a Void; the credits are DISTANCE_WITHDRAWAL_VOID deltas.
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL AND state = 'VOIDED'")).ShouldBe(1);
        (await ScalarAsync<string>(data, $"SELECT kind FROM pol.policy_transaction WHERE transaction_id = '{transactionId}'")).ShouldBe("VOID");
        var deltas = await RowsAsync(data, $"SELECT charge_type, amount, transaction_kind, cancellation_source, treatment_rule_id FROM pol.charge_line WHERE transaction_id = '{transactionId}' ORDER BY set_index");
        deltas.Count.ShouldBe(2);
        deltas[0].Take(4).ToArray().ShouldBe(["PREM-MTPL", -430.00m, "DISTANCE_WITHDRAWAL_VOID", "DistanceWithdrawal"]);
        deltas[1].Take(4).ToArray().ShouldBe(["GR-IPT", -64.50m, "DISTANCE_WITHDRAWAL_VOID", "DistanceWithdrawal"]);
        deltas[1][4].ShouldBe("GR-TRT-IPT-WITHDRAWAL-VOID");

        // Net of the term: nothing is left of premium or IPT.
        (await ScalarAsync<decimal>(data, $"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{policy.TermId}'")).ShouldBe(0m);

        // PolicyVoided and the credit set go through the outbox; PolicyCancelled lets BIL stop billing.
        (await ScalarAsync<string>(data, $"SELECT payload->>'statutoryReason' FROM plt.outbox_message WHERE event_type = 'PolicyVoided' AND aggregate_id = '{policy.PolicyId}'")).ShouldBe("WITHDRAWAL");
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND set_id = '{transactionId}' AND payload->>'transactionKind' = 'DISTANCE_WITHDRAWAL_VOID'")).ShouldBe(2);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyCancelled' AND aggregate_id = '{policy.PolicyId}'")).ShouldBe(1);

        // Exactly once: the same (policy, channelRef) under another idempotency key returns the first result and writes nothing.
        var (again, replay) = await SubmitAsync(policy.PolicyId, received, "CHN-WD-1");
        again.StatusCode.ShouldBe(HttpStatusCode.OK, replay?.ToJsonString());
        replay!["replayed"]!.GetValue<bool>().ShouldBeTrue();
        replay.Text("voidJob.jobId").ShouldBe(jobId);
        replay.Text("voidJob.refundDue.amount").ShouldBe("494.50");
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyVoided' AND aggregate_id = '{policy.PolicyId}'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.withdrawal WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);

        // A second declaration with a new channelRef finds the term already voided.
        var (second, problem) = await SubmitAsync(policy.PolicyId, received, "CHN-WD-2");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict, problem?.ToJsonString());
    }

    [Fact]
    public async Task Late_declaration_is_refused_from_receivedAt_and_an_early_one_is_honoured_late()
    {
        var policy = await _slice.BindAsync();
        var received = _slice.Clock.Now.Plus(TimeSpan.FromDays(3));
        await _slice.AdvanceAsync(TimeSpan.FromDays(20));

        // Received on day 20: the 14-day window (bind date + 14, end of day) has closed.
        var (late, problem) = await SubmitAsync(policy.PolicyId, _slice.Clock.Now, "CHN-LATE");
        late.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem?.ToJsonString());
        problem.Text("code").ShouldBe("POL-ERR-RIGHT-EXPIRED");

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND state = 'VOIDED'")).ShouldBe(0);

        // Received on day 3 but processed on day 20: the window is judged from receivedAt, so it is honoured.
        var (early, body) = await SubmitAsync(policy.PolicyId, received, "CHN-EARLY", right: "OBJECTION");
        early.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("voidJob.right").ShouldBe("OBJECTION");
        body.Text("voidJob.refundDue.amount").ShouldBe("494.50");
        (await ScalarAsync<string>(data, $"SELECT payload->>'statutoryReason' FROM plt.outbox_message WHERE event_type = 'PolicyVoided' AND aggregate_id = '{policy.PolicyId}'")).ShouldBe("OBJECTION");
    }

    [Fact]
    public async Task Dry_run_previews_the_refund_and_writes_nothing()
    {
        var policy = await _slice.BindAsync();
        var (response, body) = await SubmitAsync(policy.PolicyId, _slice.Clock.Now, "CHN-DRY", dryRun: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("voidJob.refundDue.amount").ShouldBe("494.50");

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.withdrawal WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(0);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL AND state = 'IN_FORCE'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{policy.PolicyId}' AND event_type = 'PolicyVoided'")).ShouldBe(0);
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
