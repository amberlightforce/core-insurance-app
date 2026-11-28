using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Domain;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Compliance.Reconciliation;

/// <summary>Outcome of one reconciliation pass.</summary>
internal sealed record FiscalReconciliationOutcome(
    int Checked, int Opened, int Reopened, int Resolved, int Open, IReadOnlyList<FiscalReconciliationBreakRow> Breaks);

/// <summary>
/// <c>cmp.FiscalReconciliation.run</c> (BO-CMP-07, REQ-CMP-053, REQ-CMP-055): compares every local fiscal document of the
/// legal entity (up to a business date) with the status the channel gave it, and writes one break row per document and
/// kind. Idempotent: a re-run updates the same rows, closes breaks that no longer exist and re-opens ones that came back.
/// TODO(hardening): the channel SPI has no status query, so "channel status" is the status and identifiers the channel
/// returned at submit time; a real status pull per MARK belongs with the real myDATA adapter (open decision #15).
/// </summary>
internal sealed class FiscalReconciliationEngine(
    ComplianceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IOptions<ClockOptions> clockOptions)
{
    private const int ReportedBreaks = 200;

    public BusinessDate Today => clock.Now.ToBusinessDate(clockOptions.Value.Zone);

    public async Task<FiscalReconciliationOutcome> RunAsync(BusinessDate? upTo, bool persist, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var date = upTo ?? Today;
        var now = clock.Now;

        var documents = await db.FiscalDocuments.AsNoTracking()
            .Where(d => d.LegalEntityId == legalEntity && d.IssueDate <= date)
            .Select(d => new DocFacts(d.FiscalDocumentId, d.Status, d.Role, d.Mark, d.CorrelatedDocumentId, d.RejectionCodes))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var known = (await db.FiscalDocuments.AsNoTracking().Where(d => d.LegalEntityId == legalEntity)
            .Select(d => d.FiscalDocumentId).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(i => i.Value).ToHashSet();

        var found = new Dictionary<(Guid, string), string>();
        foreach (var d in documents)
        {
            foreach (var (kind, detail) in Detect(d, known))
            {
                found[(d.Id.Value, kind)] = detail;
            }
        }

        var checkedIds = documents.Select(d => d.Id.Value).ToHashSet();
        var existing = await db.Set<FiscalReconciliationBreakRow>().Where(b => b.LegalEntityId == legalEntity).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byKey = existing.ToDictionary(b => (b.FiscalDocumentId.Value, b.Kind));
        int opened = 0, reopened = 0, resolved = 0;
        foreach (var ((docId, kind), detail) in found)
        {
            if (!byKey.TryGetValue((docId, kind), out var row))
            {
                opened++;
                var created = new FiscalReconciliationBreakRow
                {
                    BreakId = Guid.CreateVersion7(), LegalEntityId = legalEntity, FiscalDocumentId = new FiscalDocumentId(docId), Kind = kind,
                    Detail = detail, Status = FiscalBreakStatuses.Open, DetectedAt = now, LastSeenAt = now, RunDate = date,
                };
                existing.Add(created);
                if (persist)
                {
                    db.Add(created);
                }

                continue;
            }

            if (row.Status == FiscalBreakStatuses.Resolved)
            {
                reopened++;
            }

            if (persist)
            {
                row.Status = FiscalBreakStatuses.Open;
                row.ResolvedAt = null;
                row.Detail = detail;
                row.LastSeenAt = now;
                row.RunDate = date;
            }
        }

        foreach (var row in existing.Where(b => b.Status == FiscalBreakStatuses.Open && checkedIds.Contains(b.FiscalDocumentId.Value)
                     && !found.ContainsKey((b.FiscalDocumentId.Value, b.Kind))).ToList())
        {
            resolved++;
            if (persist)
            {
                row.Status = FiscalBreakStatuses.Resolved;
                row.ResolvedAt = now;
                row.RunDate = date;
            }
        }

        if (persist)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var open = (persist
            ? existing.Where(b => b.Status == FiscalBreakStatuses.Open)
            : existing.Where(b => found.ContainsKey((b.FiscalDocumentId.Value, b.Kind)) || (b.Status == FiscalBreakStatuses.Open && !checkedIds.Contains(b.FiscalDocumentId.Value))))
            .OrderBy(b => b.DetectedAt).ThenBy(b => b.BreakId).ToList();
        return new FiscalReconciliationOutcome(documents.Count, opened, reopened, resolved, open.Count, open.Take(ReportedBreaks).ToList());
    }

    private static IEnumerable<(string Kind, string Detail)> Detect(DocFacts d, HashSet<Guid> known)
    {
        if (d.Status == FiscalDocuments.Code(FiscalDocumentStatus.Rejected))
        {
            yield return (FiscalBreakKinds.RejectedByChannel, d.RejectionCodes.Length == 0 ? "Rejected by the channel." : $"Rejected by the channel: {string.Join(", ", d.RejectionCodes)}.");
        }
        else if (d.Status == FiscalDocuments.Code(FiscalDocumentStatus.Pending))
        {
            yield return (FiscalBreakKinds.PendingAtChannel, "Not yet registered by the channel (queued).");
        }
        else if (d.Status == FiscalDocuments.Code(FiscalDocumentStatus.Registered) && string.IsNullOrWhiteSpace(d.Mark))
        {
            yield return (FiscalBreakKinds.MarkMissing, "Registered without a MARK.");
        }

        if (d.Role == FiscalDocuments.Code(FiscalDocumentRole.Cancellation) && d.Correlated is null)
        {
            yield return (FiscalBreakKinds.CorrelationBroken, "A cancellation document without the original it cancels.");
        }
        else if (d.Correlated is { } original && !known.Contains(original))
        {
            yield return (FiscalBreakKinds.CorrelationBroken, $"The original document {original} does not exist.");
        }
    }

    private sealed record DocFacts(FiscalDocumentId Id, string Status, string Role, string? Mark, Guid? Correlated, string[] RejectionCodes);
}

internal sealed record RunFiscalReconciliation(FiscalReconciliationRunRequest Request) : ICommand<FiscalReconciliationRunResponse>;

/// <summary>Internal (no HTTP): the nightly job's run. Idempotent by state, so it needs no key.</summary>
internal sealed record SweepFiscalReconciliation : ICommand<int>;

internal sealed class RunFiscalReconciliationHandler(FiscalReconciliationEngine engine, RequestContext context)
    : ICommandHandler<RunFiscalReconciliation, FiscalReconciliationRunResponse>
{
    public async Task<Result<FiscalReconciliationRunResponse>> HandleAsync(RunFiscalReconciliation command, CancellationToken cancellationToken)
    {
        // The legal entity always comes from the caller's context; an `entity` in the request cannot widen it.
        var outcome = await engine.RunAsync(command.Request.Date, persist: !context.DryRun, cancellationToken).ConfigureAwait(false);
        return new FiscalReconciliationRunResponse
        {
            Checked = outcome.Checked, Opened = outcome.Opened, Reopened = outcome.Reopened, Resolved = outcome.Resolved, Open = outcome.Open,
            Breaks = [.. outcome.Breaks.Select(b => new FiscalReconciliationRunResponse.BreakItem { FiscalDocumentId = b.FiscalDocumentId, Kind = b.Kind, Detail = b.Detail })],
        };
    }
}

internal sealed class SweepFiscalReconciliationHandler(FiscalReconciliationEngine engine) : ICommandHandler<SweepFiscalReconciliation, int>
{
    public async Task<Result<int>> HandleAsync(SweepFiscalReconciliation command, CancellationToken cancellationToken) =>
        (await engine.RunAsync(null, persist: true, cancellationToken).ConfigureAwait(false)).Open;
}

internal sealed class RunFiscalReconciliationAuditor : ICommandAuditor<RunFiscalReconciliation, FiscalReconciliationRunResponse>
{
    public CommandAuditFacts Describe(RunFiscalReconciliation command, Result<FiscalReconciliationRunResponse>? result) =>
        new()
        {
            BusinessKeys = BusinessKeys.Empty,
            Changes = AuditDiff.Compute(null, new
            {
                date = command.Request.Date?.ToString(),
                checkedDocuments = result is { IsSuccess: true } ok ? ok.Value.Checked : (int?)null,
                opened = result is { IsSuccess: true } ok2 ? ok2.Value.Opened : (int?)null,
                resolved = result is { IsSuccess: true } ok3 ? ok3.Value.Resolved : (int?)null,
            }),
        };
}
