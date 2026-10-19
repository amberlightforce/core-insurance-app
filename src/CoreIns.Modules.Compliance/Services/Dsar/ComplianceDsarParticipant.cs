using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Dsar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Compliance.Services.Dsar;

/// <summary>
/// CMP's own data in a data-subject request: fiscal documents (counterparty = the subject). Fiscal documents are
/// restrict-only with retention end UNSET_F404 (D-SL6-07); there is nothing erasable.
/// </summary>
internal sealed class ComplianceDsarParticipant(IServiceScopeFactory scopes) : IDsarParticipant
{
    private const string Basis = "Retention of fiscal documents (GDPR Art. 17(3)(b); retention period unset, F-404)";

    public string Module => "CMP";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ComplianceDbContext>();
        var rows = await CollectAsync(db, subject, ct).ConfigureAwait(false);
        var outcome = rows.Count == 0
            ? DsarRecordOutcome.NotHeld(DsarRetentionPolicy.FiscalDocument)
            : DsarRecordOutcome.Restricted(DsarRetentionPolicy.FiscalDocument, Basis, rows.Count) with { Detail = "exported" };
        return new DsarModuleResult(Module, [outcome], JsonSerializer.Serialize(new Dictionary<string, object> { [DsarRetentionPolicy.FiscalDocument] = rows }));
    }

    public Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default) =>
        ApplyAsync(subject, reason, legalBasis, retentionEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);

    public Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default) =>
        ApplyAsync(subject, "DSAR erase request", Basis, null, ct);

    private async Task<DsarModuleResult> ApplyAsync(SubjectRef subject, string reason, string legalBasis, string? retentionEnd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ComplianceDbContext>();
        var count = (await CollectAsync(db, subject, ct).ConfigureAwait(false)).Count;
        var outcome = await DsarRestrictions.RestrictAsync(db, scope.ServiceProvider.GetRequiredService<IClock>(), subject.PartyId, DsarRetentionPolicy.FiscalDocument, count, reason, legalBasis, retentionEnd, ct).ConfigureAwait(false);
        return new DsarModuleResult(Module, [outcome]);
    }

    private static async Task<List<object>> CollectAsync(ComplianceDbContext db, SubjectRef subject, CancellationToken ct)
    {
        var party = subject.PartyId;
        var rows = await db.FiscalDocuments.AsNoTracking().Where(f => f.CounterpartyPartyId == party).OrderBy(f => f.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(f => (object)new
        {
            id = f.FiscalDocumentId.Value,
            documentType = f.DocumentType,
            status = f.Status,
            issueDate = f.IssueDate.ToString(),
            total = f.Total,
            currency = f.Currency,
            series = f.Series,
            number = f.Number,
            mark = f.Mark,
            sourceType = f.SourceType,
            sourceId = f.SourceId,
        }).ToList();
    }
}
