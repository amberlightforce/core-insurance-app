using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CoreIns.Modules.Policy.Commands;

/// <summary>Helpers shared by the job commands: loading a job of the caller's legal entity, state transitions, JSON columns, errors.</summary>
internal static class JobSupport
{
    /// <summary>Records that <paramref name="actor"/> worked on the job (created, edited, quoted or bound it; SOD-UW-02).</summary>
    public static void AddParticipant(JobRow job, ActorRef actor)
    {
        var id = actor.ToString();
        if (!job.Participants.Contains(id, StringComparer.Ordinal))
        {
            job.Participants = [.. job.Participants, id];
        }
    }

    /// <summary>The caller's legal entity id (REQ-POL-035): every query filters by it; another entity's job is "not found".</summary>
    public static LegalEntityId LegalEntity(RequestContext context, ILegalEntityDirectory directory) =>
        directory.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    /// <summary>The job and one of its quote versions, tracked for update; null when not found.</summary>
    public static async Task<(JobRow Job, QuoteVersionRow Version)?> LoadAsync(
        PolicyDbContext db, LegalEntityId legalEntity, JobId jobId, int versionNo, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.JobId == jobId && j.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        var version = await db.QuoteVersions.SingleOrDefaultAsync(v => v.JobId == jobId && v.VersionNo == versionNo, cancellationToken).ConfigureAwait(false);
        return version is null ? null : (job, version);
    }

    /// <summary>Fires a job transition; a forbidden one is POL-ERR-ILLEGAL-TRANSITION (REQ-POL-004, REQ-POL-072).</summary>
    public static Result<JobState> Fire(JobRow job, JobTrigger trigger)
    {
        var from = Codes.Parse<JobState>(job.State);
        var to = JobStateModel.Machine.Fire(from, trigger);
        return to.IsSuccess
            ? to
            : DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {from} job cannot {trigger}.");
    }

    /// <summary>Fires a quote version transition.</summary>
    public static Result<QuoteState> Fire(QuoteVersionRow version, QuoteTrigger trigger)
    {
        var from = Codes.Parse<QuoteState>(version.State);
        var to = QuoteStateModel.Machine.Fire(from, trigger);
        return to.IsSuccess
            ? to
            : DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {from} quote version cannot {trigger}.");
    }

    public static DomainError NotFound(string what) => DomainError.Of(ModuleCode.POL, "NOT-FOUND", $"The {what} does not exist.");

    public static DomainError Stale() =>
        DomainError.Of(ModuleCode.POL, "STALE", "The job changed meanwhile. Load the newer version and try again.");

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, SharedKernelJson.Options);

    public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, SharedKernelJson.Options)
                                                ?? throw new InvalidOperationException($"Stored {typeof(T).Name} is null.");

    /// <summary>A stored numeric(19,4) amount without the trailing zeros of the column scale (exact: no rounding).</summary>
    public static decimal Exact(decimal stored) => stored / 1.0000000000000000000000000000m;

    public static RiskTree Tree(QuoteVersionRow version) => FromJson<RiskTree>(version.RiskTree);

    /// <summary>
    /// The Idempotency-Key for an in-process command POL sends on behalf of its own command: derived deterministically
    /// from the caller's key and the operation, so a replay of POL's command replays the callee's too.
    /// </summary>
    public static IdempotencyKey Derived(IdempotencyKey? callerKey, string operation)
    {
        if (callerKey is not { } key)
        {
            return IdempotencyKey.New();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{key.Value:D}|{operation}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return IdempotencyKey.From(new Guid(bytes));
    }
}

/// <summary>
/// The write protocol of a policy's record time (D-SL3-03 a), "lock, then stamp". Every command that records anything for an
/// existing policy first calls <see cref="AcquireAsync"/>: it locks the policy row (<c>FOR UPDATE</c>), and only then takes the
/// record time <c>t = max(IClock.Now, last_recorded_at + 1 µs)</c>, truncated to the microsecond, and stores it as the new
/// <c>last_recorded_at</c> in the same transaction. Every row the command writes (term version, segments, transaction, charge
/// lines, job state, a closing <c>recorded_to</c>) uses that one <c>t</c>; the database refuses rows stamped otherwise
/// (<c>pol.require_stamp</c>). A new policy is stamped by its own insert: <see cref="ForNewPolicy"/>.
/// <para>
/// Writers of one policy are therefore serialised and each <c>t</c> is strictly greater than every committed watermark, so a
/// reader that clamps its knownAt to the committed watermark never sees an answer change (PITFALLS 13). The loser of a race
/// waits for the lock for <see cref="PolicyOptions.LockWaitSeconds"/> and then, or on a deadlock, gets <c>POL-ERR-STALE</c>
/// (409), never a 500 (PITFALLS 15).
/// </para>
/// </summary>
internal static class PolicyWriteLock
{
    private const long TicksPerMicrosecond = 10;
    private static readonly TimeSpan Microsecond = TimeSpan.FromTicks(TicksPerMicrosecond);

    /// <summary>An instant truncated to the microsecond, the precision of the columns.</summary>
    public static Instant Truncate(Instant instant) =>
        Instant.FromUtcDateTime(new DateTime(instant.ToUtcDateTime().Ticks / TicksPerMicrosecond * TicksPerMicrosecond, DateTimeKind.Utc));

    /// <summary>The record time of a command: the clock, but strictly after the policy's watermark.</summary>
    public static Instant NextRecordTime(Instant clockNow, Instant watermark) => Instant.Max(Truncate(clockNow), watermark + Microsecond);

    /// <summary>The record time of the command that creates a policy (its watermark starts here).</summary>
    public static Instant ForNewPolicy(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Truncate(clock.Now);
    }

    /// <summary>
    /// Locks the policy of <paramref name="legalEntity"/> and returns the command's record time. Must run inside the command's
    /// transaction (the lock lives until it ends). Not found is <c>POL-ERR-NOT-FOUND</c>; a lock that could not be had in time,
    /// a deadlock or a serialisation failure is <c>POL-ERR-STALE</c>.
    /// </summary>
    public static async Task<Result<Instant>> AcquireAsync(
        PolicyDbContext db, IClock clock, TimeSpan lockWait, LegalEntityId legalEntity, PolicyId policyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
                          ?? throw new InvalidOperationException("The policy write lock needs the command's transaction.");
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = db.Database.GetDbConnection();
        var wait = (lockWait.Ticks / TimeSpan.TicksPerMillisecond).ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT set_config('lock_timeout', @wait, true)", new { wait }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var watermark = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
                "SELECT last_recorded_at FROM pol.policy WHERE policy_id = @id AND legal_entity_id = @le FOR UPDATE",
                new { id = policyId.Value, le = legalEntity.Value }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (watermark is not { } committed)
            {
                return JobSupport.NotFound("policy");
            }

            var recordTime = NextRecordTime(clock.Now, Instant.FromUtcDateTime(DateTime.SpecifyKind(committed, DateTimeKind.Utc)));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE pol.policy SET last_recorded_at = @t, record_version = record_version + 1 WHERE policy_id = @id",
                new { t = recordTime.ToUtcDateTime(), id = policyId.Value }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT set_config('lock_timeout', '0', true)", transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return recordTime;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
        {
            return JobSupport.Stale();
        }
    }
}
