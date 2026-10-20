using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Work.Services.Dsar;

/// <summary>
/// Singleton face of WRK's DSAR participant (the registry is a singleton). Every step runs in its own DI scope, so the database session and
/// transaction belong to that step alone.
/// </summary>
internal sealed class WorkDsarParticipant(IServiceScopeFactory scopes) : IDsarParticipant
{
    public string Module => "WRK";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Work(scope).ExportAsync(subject, context, ct).ConfigureAwait(false);
    }

    public async Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Work(scope).RestrictAsync(subject, reason, legalBasis, retentionEnd, ct).ConfigureAwait(false);
    }

    public async Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Work(scope).EraseAsync(subject, context, ct).ConfigureAwait(false);
    }

    private static WorkDsarWork Work(AsyncServiceScope scope) =>
        ActivatorUtilities.CreateInstance<WorkDsarWork>(scope.ServiceProvider);
}

/// <summary>
/// WRK's part in a data-subject request (D-SL6-07). Subject = a party that is the primary or a secondary object of an activity (record class
/// <see cref="TaskClass"/>). Export lists those activities. Restrict records a <c>wrk.dsar_restriction</c> row. Erase retains and restricts them:
/// the activities are the supervisory record of who decided what, so they are kept with retention end <see cref="DsarRetention.UnsetF404"/>.
/// TODO(hardening): scrub the free-text note and reason of the subject's closed activities on erase, and match subjects named only inside a note.
/// </summary>
internal sealed class WorkDsarWork(DbSession session, WorkDbContext db, IClock clock) : IDsarParticipant
{
    /// <summary>Record class: staff tasks that concern the data subject.</summary>
    public const string TaskClass = "WORK_TASK";

    private const string Basis = "Supervisory record of staff decisions on the subject's policy and claims administration; retention period undefined (F-404), data retained and restricted";

    public string Module => "WRK";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var id = subject.PartyId.Value;
        var rows = await db.Activities.AsNoTracking().Where(a => a.SubjectPartyId == id).OrderBy(a => a.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        var tasks = rows.Select(a => new
        {
            activityId = a.ActivityId,
            number = a.Number,
            patternCode = a.PatternCode,
            title = a.Title,
            state = a.State,
            primaryObject = $"{a.PrimaryModule}/{a.PrimaryType}/{a.PrimaryId}",
            assignee = $"{a.AssigneeType}:{a.AssigneeId}",
            createdAt = a.CreatedAt.ToString(),
            targetAt = a.TargetAt?.ToString(),
            closedAt = a.ClosedAt?.ToString(),
            outcome = a.Outcome,
            note = a.Note,
            reason = a.CloseReason,
        }).ToList();
        var restrictions = await db.Set<DsarRestrictionRow>().AsNoTracking().Where(r => r.PartyId == id && r.RecordClass == TaskClass)
            .Select(r => new { recordClass = r.RecordClass, legalBasis = r.LegalBasis, retentionEnd = r.RetentionEnd }).ToListAsync(ct).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new { module = "WRK", partyId = id, tasks, restrictions }, SharedKernelJson.Options);
        var outcome = rows.Count == 0 ? NothingHeld() : new DsarRecordOutcome(TaskClass, DsarOutcomeKind.NotHeld, rows.Count, Detail: "EXPORTED");
        return new DsarModuleResult(Module, [outcome], payload);
    }

    public async Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var end = retentionEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DsarModuleResult(Module, [await RestrictClassAsync(subject, reason, legalBasis, end, ct).ConfigureAwait(false)]);
    }

    public async Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(context);
        return new DsarModuleResult(Module, [await RestrictClassAsync(subject, "erase request: retained", Basis, null, ct).ConfigureAwait(false)]);
    }

    private async Task<DsarRecordOutcome> RestrictClassAsync(SubjectRef subject, string reason, string legalBasis, string? retentionEnd, CancellationToken ct)
    {
        var id = subject.PartyId.Value;
        var transaction = await session.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var count = await db.Activities.CountAsync(a => a.SubjectPartyId == id, ct).ConfigureAwait(false);
            var outcome = await DsarRestrictions.RestrictAsync(db, clock, subject.PartyId, TaskClass, count, reason, legalBasis, retentionEnd, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return outcome.Kind == DsarOutcomeKind.NotHeld ? NothingHeld() : outcome;
        }
    }

    private static DsarRecordOutcome NothingHeld() => DsarRecordOutcome.NotHeld(TaskClass) with { Detail = "No task concerns the data subject" };
}
