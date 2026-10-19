using CoreIns.SharedKernel.Dsar;

namespace CoreIns.Modules.Underwriting.Services.Dsar;

/// <summary>
/// UW's part in a data-subject request. The module stores no party identifier: its rows are keyed by job, quote, evaluation or
/// worksheet ids and reach a person only through POL (policyholder), whose participant exports and restricts the quote data.
/// Every step therefore reports each record class as NOT_HELD with an explicit detail, never a silent skip.
/// TODO(hardening): resolve the subject's job and quote ids from POL and restrict the matching rows here once a cross-module
/// subject-to-job lookup exists; until then risk snapshots held in this module are unrestricted.
/// </summary>
internal sealed class UnderwritingDsarParticipant : IDsarParticipant
{
    private static readonly string[] Classes = ["UW_EVALUATION", "UW_ISSUE"];

    public string Module => "UW";

    public Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Task.FromResult(new DsarModuleResult(Module, Outcomes(), $$"""{"module":"UW","partyId":"{{subject.PartyId.Value}}","records":[]}"""));
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
        Classes.Select(c => new DsarRecordOutcome(c, DsarOutcomeKind.NotHeld, Detail: "No party key in this module; reached through POL jobs")).ToList();
}
