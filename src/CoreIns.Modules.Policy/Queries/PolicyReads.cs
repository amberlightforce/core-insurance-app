using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Commands.Change;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using CoreIns.SharedKernel.StateMachines;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Queries;

/// <summary>
/// BO-POL-02: the read APIs the policy file and the downstream feeds need beyond <c>pol.Policy.get</c>: <c>pol.Policy.getMany</c>,
/// <c>pol.StatusModel.get</c>, <c>pol.EffectiveDate.limits</c> and <c>pol.Segment.changes</c>. All are scoped to the caller's legal entity.
/// </summary>
internal sealed class PolicyReads(
    PolicyReader reader, DbSession session, PolicyDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock,
    IOptions<PolicyOptions> options, IOptions<ChangeOptions> changeOptions)
{
    /// <summary>The most ids <c>pol.Policy.getMany</c> answers in one call.</summary>
    public const int MaxIds = 50;

    /// <summary>pol.Policy.getMany: each id answered as <c>pol.Policy.get</c>; ids unknown in this legal entity come back in <c>NotFound</c>.</summary>
    public async Task<Result<PolicyGetManyResponse>> GetManyAsync(IReadOnlyList<string> rawIds, Instant validAt, Instant knownAt, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        foreach (var raw in rawIds)
        {
            if (!Guid.TryParse(raw, out var id))
            {
                return Invalid("ids must be UUIDs.");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count is 0 or > MaxIds)
        {
            return Invalid($"Give between 1 and {MaxIds} policy ids.");
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var code = context.LegalEntity!.Value.Value;
        List<PolicyGetResponse> policies = [];
        List<PolicyId> missing = [];
        foreach (var id in ids)
        {
            var answered = await reader.GetPolicyEffectiveAsync(legalEntity, code, id, validAt, knownAt, cancellationToken).ConfigureAwait(false);
            if (answered is var (response, effectiveKnownAt))
            {
                policies.Add(response with { EffectiveKnownAt = effectiveKnownAt });
            }
            else
            {
                missing.Add(new PolicyId(id));
            }
        }

        return new PolicyGetManyResponse { Policies = policies, NotFound = [.. missing.Select(m => m.Value)] };
    }

    /// <summary>pol.StatusModel.get: the canonical state tables as built into the module (job, quote, policy term).</summary>
    public static StatusModelGetResponse StatusModel() => new()
    {
        Table = [Table("Job", JobStateModel.Machine), Table("Quote", QuoteStateModel.Machine), Table("PolicyTerm", PolicyTermStateModel.Machine)],
    };

    private static StatusModelTable Table<TState, TTrigger>(string entity, StateMachine<TState, TTrigger> machine)
        where TState : struct, Enum
        where TTrigger : struct, Enum => new()
    {
        Entity = entity,
        States = [.. machine.States.Select(s => s.ToString())],
        InitialStates = [.. machine.InitialStates.Select(s => s.ToString())],
        TerminalStates = [.. machine.TerminalStates.Select(s => s.ToString())],
        Transitions = [.. machine.Transitions.Select(t => new StatusModelTransition { From = t.From.ToString(), Trigger = t.Trigger.ToString(), To = t.To.ToString() })],
    };

    /// <summary>
    /// pol.EffectiveDate.limits: the range a change may be effective in for the policy's term at the time of the call, for the given role
    /// (default: the caller's roles; the most permissive applies), the same rule the change commands enforce (D-SL3-08).
    /// </summary>
    public async Task<Result<EffectiveDateLimitsResponse>> LimitsAsync(Guid policyId, string? txnType, string? role, CancellationToken cancellationToken)
    {
        if (txnType is not null && !string.Equals(txnType, "CHANGE", StringComparison.OrdinalIgnoreCase))
        {
            // TODO(hardening): limits per transaction type (cancellation, reinstatement) once their rules are configurable.
            return Invalid("txnType: only CHANGE limits are defined.");
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var policy = new PolicyId(policyId);
        if (!await db.Policies.AsNoTracking().AnyAsync(p => p.PolicyId == policy && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false))
        {
            return JobSupport.NotFound("policy");
        }

        var now = clock.Now;
        var terms = await db.Terms.AsNoTracking().Where(t => t.PolicyId == policy && t.LegalEntityId == legalEntity && t.RecordedTo == null)
            .OrderBy(t => t.TermNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        var term = terms.FirstOrDefault(t => t.ValidFrom <= now && now < t.ValidTo)
                   ?? terms.FirstOrDefault(t => t.ValidFrom > now)
                   ?? terms.LastOrDefault();
        if (term is null)
        {
            return JobSupport.NotFound("term");
        }

        IReadOnlyCollection<string> roles = string.IsNullOrWhiteSpace(role) ? context.Roles : [role];
        var settings = changeOptions.Value;
        var (earliest, latest) = ChangeLimits.Range(settings, roles, now, term, options.Value.Zone);
        var days = roles.Where(settings.BackdateDaysByRole.ContainsKey).Select(r => settings.BackdateDaysByRole[r]).DefaultIfEmpty(settings.BackdateDaysDefault).Max();
        // TODO(hardening): overrideAvailable once a limit-override permission and its approval flow exist (REQ-POL-136).
        return new EffectiveDateLimitsResponse { Earliest = earliest, Latest = latest, BackdateDays = days, OverrideAvailable = false };
    }

    /// <summary>
    /// pol.Segment.changes: segment versions in the order they last changed (recordedTo when closed, else recordedFrom), keyset-paged;
    /// a version that is later closed re-appears with its closure. <paramref name="since"/> / <paramref name="sinceTransactionId"/> only seed the first page.
    /// </summary>
    public async Task<Result<SegmentChangesPage>> SegmentChangesAsync(
        string? cursor, int? limit, Instant? since, Guid? sinceTransactionId, CancellationToken cancellationToken)
    {
        if (!KeysetPaging.TryTake(limit, KeysetPaging.DefaultPageSize, out var take) || !KeysetPaging.TryDecode(cursor, 2, out var parts))
        {
            return Invalid("cursor is malformed or limit is outside 1..200.");
        }

        DateTime? afterAt = null;
        Guid? afterId = null;
        if (parts is not null)
        {
            if (!KeysetPaging.TryLong(parts[0], out var ticks) || !Guid.TryParse(parts[1], out var id) || ticks < 0 || ticks > DateTime.MaxValue.Ticks)
            {
                return Invalid("cursor is malformed or limit is outside 1..200.");
            }

            afterAt = new DateTime(ticks, DateTimeKind.Utc);
            afterId = id;
        }

        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sinceAt = since?.ToUtcDateTime();
        if (parts is null && sinceTransactionId is { } transaction)
        {
            var recorded = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
                "SELECT recorded_at FROM pol.policy_transaction WHERE transaction_id = @id AND legal_entity_id = @le",
                new { id = transaction, le = legalEntity.Value }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (recorded is null)
            {
                return JobSupport.NotFound("transaction");
            }

            var recordedUtc = Utc(recorded.Value);
            sinceAt = sinceAt is { } explicitSince && explicitSince > recordedUtc ? explicitSince : recordedUtc;
        }

        var rows = (await connection.QueryAsync<SegmentChangeRecord>(new CommandDefinition(
            """
            SELECT segment_id AS SegmentId, term_id AS TermId, policy_id AS PolicyId, transaction_id AS TransactionId, valid_from AS ValidFrom,
                   valid_to AS ValidTo, recorded_from AS RecordedFrom, recorded_to AS RecordedTo, COALESCE(recorded_to, recorded_from) AS ChangedAt,
                   snapshot_hash AS SnapshotHash, worksheet_id AS WorksheetId
              FROM pol.segment
             WHERE legal_entity_id = @le
               AND (@since::timestamptz IS NULL OR COALESCE(recorded_to, recorded_from) >= @since)
               AND (@afterAt::timestamptz IS NULL OR (COALESCE(recorded_to, recorded_from), segment_id) > (@afterAt::timestamptz, @afterId::uuid))
             ORDER BY COALESCE(recorded_to, recorded_from), segment_id
             LIMIT @take
            """,
            new { le = legalEntity.Value, since = sinceAt, afterAt, afterId, take = take + 1 }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var (page, next) = KeysetPaging.Slice(rows, take, r => KeysetPaging.Encode(KeysetPaging.Part(Utc(r.ChangedAt).Ticks), KeysetPaging.Part(r.SegmentId)));
        return new SegmentChangesPage
        {
            Items = [.. page.Select(r => new SegmentChangesItem
            {
                SegmentId = new SegmentId(r.SegmentId),
                TermId = new PolicyTermId(r.TermId),
                PolicyId = new PolicyId(r.PolicyId),
                TransactionId = new PolicyTransactionId(r.TransactionId),
                ValidFrom = JobReader.Time(r.ValidFrom),
                ValidTo = JobReader.Time(r.ValidTo),
                RecordedFrom = JobReader.Time(r.RecordedFrom),
                RecordedTo = r.RecordedTo is { } to ? JobReader.Time(to) : null,
                ChangedAt = JobReader.Time(r.ChangedAt),
                SnapshotHash = r.SnapshotHash,
                WorksheetId = r.WorksheetId,
            })],
            NextCursor = next,
            Limit = take,
        };
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DomainError Invalid(string message) => DomainError.Of(ModuleCode.POL, "VALIDATION", message);

    private sealed class SegmentChangeRecord
    {
        public Guid SegmentId { get; set; }

        public Guid TermId { get; set; }

        public Guid PolicyId { get; set; }

        public Guid TransactionId { get; set; }

        public DateTime ValidFrom { get; set; }

        public DateTime ValidTo { get; set; }

        public DateTime RecordedFrom { get; set; }

        public DateTime? RecordedTo { get; set; }

        public DateTime ChangedAt { get; set; }

        public string SnapshotHash { get; set; } = string.Empty;

        public string? WorksheetId { get; set; }
    }
}
