using CoreIns.Platform.Dsar;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Dsar;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Rating.Services.Dsar;

/// <summary>
/// RAT's part in a data-subject request. The module stores no party identifier: worksheets are indexed by job and quote id, so the
/// subject's jobs and quotes are resolved from POL (policyholder) and the worksheets matched through <c>rat.worksheet_index</c>.
/// Export returns them; restrict and erase never destroy them (they are the evidence behind a quoted premium) and report RESTRICTED
/// with the retention reason.
/// TODO(hardening): persist a RAT-side restriction marker (dsar_restriction table + migration) and enforce it in readers; until then the
/// worksheets are retained and untouched but not blocked from other reads. The DSAR map treats RAT as export-only.
/// </summary>
internal sealed class RatingDsarParticipant(IServiceScopeFactory scopes) : IDsarParticipant
{
    private const string Basis = "Rating worksheets: legal obligation to retain quote evidence (GDPR Art 17(3)(b)); retention period pending F-404";
    private const string WorksheetClass = "RATING_WORKSHEET";

    public string Module => "RAT";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var worksheets = await CollectAsync(subject, ct).ConfigureAwait(false);
        var payload = $$$"""{"module":"RAT","partyId":"{{{subject.PartyId.Value}}}","records":{"{{{WorksheetClass}}}":{{{worksheets.Json}}}}}""";
        return new DsarModuleResult(Module, [Held(worksheets.Count, "exported")], payload);
    }

    public async Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var worksheets = await CollectAsync(subject, ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, [Held(worksheets.Count, null)]);
    }

    public async Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var worksheets = await CollectAsync(subject, ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, [Held(worksheets.Count, DsarRetentionPolicy.NotErasedDetail(Basis))]);
    }

    private static DsarRecordOutcome Held(int count, string? detail) =>
        count == 0 ? DsarRecordOutcome.NotHeld(WorksheetClass) : DsarRecordOutcome.Restricted(WorksheetClass, Basis, count) with { Detail = detail };

    private async Task<(string Json, int Count)> CollectAsync(SubjectRef subject, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();
        var (jobs, quotes) = await DsarSubjectLookup.JobsAndQuotesAsync(session, subject.PartyId.Value, ct).ConfigureAwait(false);
        if (jobs.Length == 0 && quotes.Length == 0)
        {
            return ("[]", 0);
        }

        return await DsarSubjectLookup.JsonRowsAsync(
            session,
            "SELECT w.worksheet_id, i.job_id, i.quote_id, i.mode, i.retention_state, w.data_status, w.body, w.created_at " +
            "FROM rat.worksheet_index i JOIN rat.worksheet w ON w.worksheet_id = i.worksheet_id " +
            "WHERE i.job_id = ANY(@jobs) OR i.quote_id = ANY(@quotes) ORDER BY w.created_at",
            ct, ("jobs", jobs), ("quotes", quotes)).ConfigureAwait(false);
    }
}
