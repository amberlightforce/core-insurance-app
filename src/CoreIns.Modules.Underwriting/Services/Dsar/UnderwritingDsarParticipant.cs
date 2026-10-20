using CoreIns.Platform.Dsar;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Dsar;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Underwriting.Services.Dsar;

/// <summary>
/// UW's part in a data-subject request. The module stores no party identifier: evaluations and issues are keyed by job id, so
/// the subject's jobs are resolved from POL (policyholder) and the rows matched by job. Export returns them; restrict and erase
/// never destroy them (underwriting decisions are regulated evidence) and report RESTRICTED with the retention reason.
/// TODO(hardening): persist a UW-side restriction marker (dsar_restriction table + migration) and enforce it in readers; until then the
/// rows are retained and untouched but not blocked from other reads.
/// </summary>
internal sealed class UnderwritingDsarParticipant(IServiceScopeFactory scopes) : IDsarParticipant
{
    private const string Basis = "Underwriting decision records: legal obligation to retain (GDPR Art 17(3)(b)); retention period pending F-404";
    private const string EvaluationClass = "UW_EVALUATION";
    private const string IssueClass = "UW_ISSUE";

    public string Module => "UW";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var (evaluations, issues) = await CollectAsync(subject, ct).ConfigureAwait(false);
        var payload = $$$"""{"module":"UW","partyId":"{{{subject.PartyId.Value}}}","records":{"{{{EvaluationClass}}}":{{{evaluations.Json}}},"{{{IssueClass}}}":{{{issues.Json}}}}}""";
        return new DsarModuleResult(Module, [Held(EvaluationClass, evaluations.Count, "exported"), Held(IssueClass, issues.Count, "exported")], payload);
    }

    public async Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return await RetainAsync(subject, null, ct).ConfigureAwait(false);
    }

    public async Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return await RetainAsync(subject, DsarRetentionPolicy.NotErasedDetail(Basis), ct).ConfigureAwait(false);
    }

    private async Task<DsarModuleResult> RetainAsync(SubjectRef subject, string? detail, CancellationToken ct)
    {
        var (evaluations, issues) = await CollectAsync(subject, ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, [Held(EvaluationClass, evaluations.Count, detail), Held(IssueClass, issues.Count, detail)]);
    }

    private static DsarRecordOutcome Held(string recordClass, int count, string? detail) =>
        count == 0 ? DsarRecordOutcome.NotHeld(recordClass) : DsarRecordOutcome.Restricted(recordClass, Basis, count) with { Detail = detail };

    private async Task<((string Json, int Count) Evaluations, (string Json, int Count) Issues)> CollectAsync(SubjectRef subject, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        var (jobs, _) = await DsarSubjectLookup.JobsAndQuotesAsync(session, subject.PartyId.Value, ct).ConfigureAwait(false);
        if (jobs.Length == 0)
        {
            return (("[]", 0), ("[]", 0));
        }

        var evaluations = await DsarSubjectLookup.JsonRowsAsync(
            session,
            "SELECT evaluation_id, job_id, checkpoint, rule_set_code, rule_set_version, outcome, lane, facts, created_at FROM uw.evaluation WHERE job_id = ANY(@jobs) ORDER BY created_at",
            ct, ("jobs", jobs)).ConfigureAwait(false);
        var issues = await DsarSubjectLookup.JsonRowsAsync(
            session,
            "SELECT issue_id, job_id, issue_type, blocking_point, severity, status, message_en, message_el, created_at, closed_at FROM uw.issue WHERE job_id = ANY(@jobs) ORDER BY created_at",
            ct, ("jobs", jobs)).ConfigureAwait(false);
        return (evaluations, issues);
    }
}
