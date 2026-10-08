using System.Globalization;
using System.Net;
using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Temporal;

/// <summary>An injectable clock for the writer under test.</summary>
internal sealed class FakeClock(Instant now) : IClock
{
    public Instant Now { get; set; } = now;
}

/// <summary>Releases every participant at once, once all have arrived (a real barrier, no sleeps).</summary>
internal sealed class AsyncBarrier(int participants)
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public Task SignalAndWaitAsync()
    {
        if (Interlocked.Increment(ref _arrived) == participants)
        {
            _gate.TrySetResult();
        }

        return _gate.Task;
    }
}

/// <summary>A bound policy, the slice host over the real database, and the raw SQL / writer helpers the temporal tests share.</summary>
public abstract class TemporalTestBase(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    internal sealed record Bound(string PartyId, Guid PolicyId, string PolicyNumber, Guid TermId, DateTime Start, DateTime End);

    internal PolicySlice Slice { get; private set; } = null!;

    protected PostgresFixture Database { get; } = database;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Slice = new PolicySlice(Database.AppConnectionString);
        await Slice.SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Slice.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    internal static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);

    internal static string Q(string value) => Uri.EscapeDataString(value);

    internal static DateTime Utc(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    internal async Task<Bound> BindAsync()
    {
        var party = await Slice.CreatePartyAsync();
        var (jobId, _, _) = await Slice.DraftAsync(party, DateTimeOffset.UtcNow.AddDays(2));
        (await Slice.QuoteAsync(jobId)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (response, bind) = await Slice.BindAsync(jobId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, bind?.ToJsonString());
        var policyId = bind.Text("policyId");
        var (_, policy) = await SendAsync(Slice.Client, HttpMethod.Get, $"/api/pol/v1/policies/{policyId}?validAt={Q(Iso(DateTime.UtcNow.AddDays(30)))}");
        return new Bound(party, Guid.Parse(policyId), bind.Text("policyNumber"), Guid.Parse(bind.Text("termId")), Utc(policy.Text("term.period.from")), Utc(policy.Text("term.period.to")));
    }

    internal async Task<(HttpResponseMessage Response, string Raw)> RawAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, Underwriter);
        var response = await Slice.Client.SendAsync(request, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The committed record-time watermark, read as the superuser.</summary>
    internal async Task<Instant> WatermarkAsync(Guid policyId)
    {
        await using var source = NpgsqlDataSource.Create(Database.SuperuserConnectionString);
        await using var command = source.CreateCommand("SELECT last_recorded_at FROM pol.policy WHERE policy_id = $1");
        command.Parameters.AddWithValue(policyId);
        return Instant.FromUtcDateTime(DateTime.SpecifyKind((DateTime)(await command.ExecuteScalarAsync(Ct))!, DateTimeKind.Utc));
    }

    /// <summary>The watermark in microseconds since the epoch, the unit of snapshot references.</summary>
    internal async Task<long> WatermarkMicrosAsync(Guid policyId)
    {
        await using var source = NpgsqlDataSource.Create(Database.SuperuserConnectionString);
        await using var command = source.CreateCommand("SELECT (extract(epoch FROM last_recorded_at) * 1000000)::bigint FROM pol.policy WHERE policy_id = $1");
        command.Parameters.AddWithValue(policyId);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>
    /// Runs a writer as a command does: its own scope and transaction, the policy lock and record time, an optional body that
    /// writes under that record time, an optional step while the transaction is still open, then commit. A failed lock rolls back.
    /// </summary>
    internal async Task<Result<Instant>> WriteAsync(
        Guid policyId, IClock clock, TimeSpan lockWait, Func<PolicyDbContext, Instant, Task>? body = null, Func<Instant, Task>? whileOpen = null)
    {
        await using var scope = Slice.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PolicyDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var locked = await PolicyWriteLock.AcquireAsync(db, clock, lockWait, new LegalEntityId(Guid.Parse(ApiHostFactory.LegalEntityId)), new PolicyId(policyId), Ct);
        if (locked.IsFailure)
        {
            await transaction.RollbackAsync(Ct);
            return locked;
        }

        if (body is not null)
        {
            await body(db, locked.Value);
        }

        if (whileOpen is not null)
        {
            await whileOpen(locked.Value);
        }

        await transaction.CommitAsync(Ct);
        return locked;
    }

    /// <summary>
    /// Re-cuts the term's current segment under the writer's record time <paramref name="t"/>: the old one is closed at t and
    /// replaced, together with the transaction that records it. <paramref name="split"/> cuts it in two at day 60 with the same
    /// content; <paramref name="changeContent"/> drops the coverages (different content, different risk hash).
    /// </summary>
    internal static async Task RecutSegmentAsync(PolicyDbContext db, Guid termId, DateTime t, bool split, bool changeContent, int sequence)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO pol.policy_transaction (transaction_id, policy_id, term_id, job_id, legal_entity_id, kind, sequence, effective_at, recorded_at,
                configuration_hash, artefact_hash, resolution_hash, intent, premium, taxes, total, currency, actor, correlation_id, origin)
            SELECT gen_random_uuid(), policy_id, term_id, gen_random_uuid(), legal_entity_id, 'CHANGE', {2}, effective_at, {1},
                configuration_hash, artefact_hash, resolution_hash, intent, 0, 0, 0, currency, actor, correlation_id, origin
              FROM pol.policy_transaction WHERE term_id = {0} AND sequence = 1;
            UPDATE pol.segment SET recorded_to = {1} WHERE term_id = {0} AND recorded_to IS NULL;
            """, [termId, t, sequence]);
#pragma warning disable EF1002 // test-owned constants only; the values travel as parameters
        var snapshot = changeContent ? "jsonb_set(s.snapshot, ARRAY['coverages'], '[]'::jsonb)" : "s.snapshot";
        var hash = changeContent ? $"'{new string('b', 64)}'" : "s.snapshot_hash";
        var slices = split
            ? "(s.valid_from, s.valid_from + interval '60 days'), (s.valid_from + interval '60 days', s.valid_to)"
            : "(s.valid_from, s.valid_to)";
        await db.Database.ExecuteSqlRawAsync(
            $$"""
            INSERT INTO pol.segment (segment_id, term_id, policy_id, transaction_id, legal_entity_id, valid_from, valid_to, recorded_from, snapshot_hash, snapshot)
            SELECT gen_random_uuid(), s.term_id, s.policy_id, x.transaction_id, s.legal_entity_id, c.vf, c.vt, {1}, {{hash}}, {{snapshot}}
              FROM pol.segment s
              CROSS JOIN LATERAL (VALUES {{slices}}) AS c(vf, vt)
              CROSS JOIN LATERAL (SELECT transaction_id FROM pol.policy_transaction WHERE term_id = {0} AND sequence = {2}) x
             WHERE s.term_id = {0} AND s.recorded_to = {1}
            """, [termId, t, sequence]);
#pragma warning restore EF1002
    }

    internal static PolicySnapshots Snapshots(AsyncServiceScope scope)
    {
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        return scope.ServiceProvider.GetRequiredService<PolicySnapshots>();
    }

    internal NpgsqlDataSource AppSource() => NpgsqlDataSource.Create(Database.AppConnectionString);

    internal static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
