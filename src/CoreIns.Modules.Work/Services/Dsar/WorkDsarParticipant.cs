using CoreIns.SharedKernel.Dsar;

namespace CoreIns.Modules.Work.Services.Dsar;

/// <summary>
/// WRK's part in a data-subject request. The Work module stores no tasks, queues or assignments yet (placeholder until NX-HD-05),
/// so every step reports its record class as NOT_HELD with an explicit detail, never a silent skip and never NOT_BUILT.
/// TODO(hardening): once NX-HD-05 lands, export and restrict task rows that reference the subject (via DsarRestrictions) and report real counts.
/// </summary>
internal sealed class WorkDsarParticipant : IDsarParticipant
{
    internal const string TaskClass = "WORK_TASK";

    public string Module => "WRK";

    public Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Task.FromResult(new DsarModuleResult(Module, Outcomes(), $$"""{"module":"WRK","partyId":"{{subject.PartyId.Value}}","records":[]}"""));
    }

    public Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Task.FromResult(new DsarModuleResult(Module, Outcomes()));
    }

    public Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Task.FromResult(new DsarModuleResult(Module, Outcomes()));
    }

    private static List<DsarRecordOutcome> Outcomes() =>
        [DsarRecordOutcome.NotHeld(TaskClass) with { Detail = "The Work module holds no task data yet" }];
}
