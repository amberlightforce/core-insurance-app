using System.Text.Json.Nodes;
using CoreIns.IntegrationTests.Party;
using CoreIns.IntegrationTests.Product;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using Npgsql;
using static CoreIns.IntegrationTests.Finance.FinanceSlice;

namespace CoreIns.IntegrationTests.Finance;

/// <summary>SL6-FIN-OOS-GUARD: delta sets post as one journal once complete (D4); a locked period takes no posting, a correction posts in the first open one (G5, REQ-POL-108).</summary>
public sealed class OosGuardTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Day = "2026-11-02";
    private const string SetDay = "2026-10-05";
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;
    private FinanceSlice _slice = null!;
    private NpgsqlDataSource _db = null!;
    private string? _artefactHash;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        _slice = new FinanceSlice(_factory);
        _db = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var (response, body) = await ProductApi.ImportAsync(_client, ProductApi.Seed("MOTOR-FIN"));
        response.IsSuccessStatusCode.ShouldBeTrue(body?.ToJsonString());
        _artefactHash = body.Text("artefactHash");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<Policy> BoundPolicyAsync()
    {
        var policy = Policy.New($"POL{Random.Shared.Next(100_000_000, 999_999_999)}", "MOTOR-FIN", _artefactHash!);
        await _slice.PolicyBoundAsync(policy);
        await _slice.DrainAsync();
        return policy;
    }

    private static JsonObject Charge(Policy policy, Guid account, string side, string ledgerAccount, string amount, Guid chargeId)
    {
        var line = Line(ledgerAccount, side, amount, policy, account, "PREM-MTPL", "PREMIUM", "MTPL");
        line["dimensions"]!["chargeId"] = chargeId.ToString();
        return line;
    }

    [Fact]
    public async Task A_set_of_three_deltas_posts_one_balanced_journal_once_complete_and_a_replay_adds_none()
    {
        var policy = await BoundPolicyAsync();
        var account = Guid.CreateVersion7();
        var set = Guid.CreateVersion7();
        for (var index = 1; index <= 3; index++)
        {
            var charge = Guid.CreateVersion7();
            await _slice.EntryInSetAsync(account, "WRITTEN", SetDay, new EventSet(set, 3, index),
                Charge(policy, account, "DEBIT", "LA-01", "10.00", charge), Charge(policy, account, "CREDIT", "LA-04", "10.00", charge));
            await _slice.DrainAsync();
            var journals = await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_entry WHERE source_ref = 'set:{set}'");
            journals.ShouldBe(index < 3 ? 0 : 1);
        }

        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.business_event WHERE set_id = '{set}' AND status = 'POSTED'")).ShouldBe(3);
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_line l JOIN fin.journal_entry j USING (journal_id) WHERE j.source_ref = 'set:{set}'")).ShouldBe(6);
        (await ScalarAsync<long>(_db, $"""
            SELECT count(*) FROM (SELECT l.journal_id FROM fin.journal_line l JOIN fin.journal_entry j USING (journal_id) WHERE j.source_ref = 'set:{set}'
                                  GROUP BY l.journal_id HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0) t
            """)).ShouldBe(0);

        var before = await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry");
        await database.ExecuteAsSuperuserAsync(
            "UPDATE plt.outbox_message SET status = 'Pending', dispatched_at = NULL; DELETE FROM plt.processed_event WHERE handler LIKE 'FIN.%'",
            TestContext.Current.CancellationToken);
        await _slice.DrainAsync();
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry")).ShouldBe(before);
    }

    [Fact]
    public async Task A_locked_month_takes_no_posting_and_the_correction_posts_in_the_first_open_period_with_its_reference()
    {
        await database.ExecuteAsSuperuserAsync(
            $"INSERT INTO fin.financial_period (period_id, legal_entity_id, period_code, status, created_at) VALUES ('{Guid.CreateVersion7()}', '{ApiHostFactory.LegalEntityId}', '2026-11', 'LOCKED', now()) ON CONFLICT (legal_entity_id, period_code) DO UPDATE SET status = 'LOCKED'",
            TestContext.Current.CancellationToken);
        var policy = await BoundPolicyAsync();
        var account = Guid.CreateVersion7();

        // An ordinary entry dated in the locked month is refused: an intake exception, no journal.
        var ordinary = await _slice.EntryAsync(account, "WRITTEN", Day,
            Charge(policy, account, "DEBIT", "LA-01", "25.00", Guid.CreateVersion7()), Charge(policy, account, "CREDIT", "LA-04", "25.00", Guid.CreateVersion7()));
        await _slice.DrainAsync();
        (await ScalarAsync<string>(_db, $"SELECT status || ':' || exception_reason FROM fin.business_event WHERE source_event_id = '{ordinary.EventId.Value}'")).ShouldBe("SUSPENDED:PERIOD_LOCKED");
        (await ScalarAsync<long>(_db, $"SELECT count(*) FROM fin.journal_entry WHERE '{ordinary.EventId.Value}'::uuid = ANY(source_event_ids)")).ShouldBe(0);

        // A correction (its ChargeDeltaEmitted carries correctionOf) posts on the first day of the next open month.
        var corrected = Guid.CreateVersion7();
        var oosSet = Guid.CreateVersion7();
        var charge = Guid.CreateVersion7();
        var delta = Sample("pol", "ChargeDeltaEmitted");
        delta["chargeId"] = charge.ToString();
        delta["termId"] = policy.TermId.ToString();
        delta["correctionOf"] = corrected.ToString();
        delta["oosSetId"] = oosSet.ToString();
        await _slice.PublishAsync(ChargeDeltaEmittedV1.Descriptor, "Policy", policy.PolicyId.ToString(), delta,
            BusinessKeys.Empty.With("policyId", policy.PolicyId.ToString()).With("chargeId", charge.ToString())
                .With("policyTermId", policy.TermId.ToString()).With("transactionId", policy.TransactionId.ToString()),
            new EventSet(policy.TransactionId, 1, 1));
        await _slice.DrainAsync(); // the delta context is in before the entry that follows it
        var entry = await _slice.EntryAsync(account, "WRITTEN", Day,
            Charge(policy, account, "DEBIT", "LA-01", "40.00", charge), Charge(policy, account, "CREDIT", "LA-04", "40.00", charge));
        await _slice.DrainAsync();

        (await ScalarAsync<string>(_db, $"SELECT status || ':' || coalesce(exception_reason, '') || ':' || coalesce(exception_detail, '') FROM fin.business_event WHERE source_event_id = '{entry.EventId.Value}'")).ShouldBe("POSTED::");
        (await ScalarAsync<string>(_db, $"""
            SELECT j.accounting_date::text || ':' || j.business_date::text || ':' || p.period_code || ':' || j.correction_of || ':' || j.oos_set_id
              FROM fin.journal_entry j JOIN fin.financial_period p USING (period_id) WHERE '{entry.EventId.Value}'::uuid = ANY(j.source_event_ids)
            """)).ShouldBe($"2026-12-01:{Day}:2026-12:{corrected}:{oosSet}");
        (await ScalarAsync<long>(_db, "SELECT count(*) FROM fin.journal_entry j JOIN fin.financial_period p USING (period_id) WHERE p.status = 'LOCKED'")).ShouldBe(0);
    }
}
