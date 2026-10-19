using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Dsar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Migration.Services.Dsar;

/// <summary>
/// MIG's part in a data-subject request (MIG-006): the migration archive (legacy key links and the legacy payloads of
/// migrated items) is exported and restricted, never erased; claim files under retention stay restricted (D-SL6-07).
/// </summary>
internal sealed class MigrationDsarParticipant(IServiceScopeFactory scopes) : IDsarParticipant
{
    internal const string Xref = "MIGRATION_XREF";
    internal const string Archive = "MIGRATION_ARCHIVE";
    private const string Basis = "Migration audit archive of legacy data (GDPR Art. 17(3)(b); retention period unset, F-404)";

    public string Module => "MIG";

    public async Task<DsarModuleResult> ExportAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MigrationDbContext>();
        var data = await CollectAsync(db, subject, ct).ConfigureAwait(false);
        var outcomes = data.Select(d => d.Count == 0
            ? DsarRecordOutcome.NotHeld(d.Class)
            : DsarRecordOutcome.Restricted(d.Class, Basis, d.Count) with { Detail = "exported" }).ToList();
        return new DsarModuleResult(Module, outcomes, JsonSerializer.Serialize(data.ToDictionary(d => d.Class, d => d.Rows)));
    }

    public Task<DsarModuleResult> RestrictAsync(SubjectRef subject, string reason, string legalBasis, DateOnly? retentionEnd, CancellationToken ct = default) =>
        ApplyAsync(subject, reason, legalBasis, retentionEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);

    public Task<DsarModuleResult> EraseAsync(SubjectRef subject, DsarContext context, CancellationToken ct = default) =>
        ApplyAsync(subject, "DSAR erase request", Basis, null, ct);

    private async Task<DsarModuleResult> ApplyAsync(SubjectRef subject, string reason, string legalBasis, string? retentionEnd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MigrationDbContext>();
        var outcomes = new List<DsarRecordOutcome>();
        foreach (var d in await CollectAsync(db, subject, ct).ConfigureAwait(false))
        {
            outcomes.Add(await DsarRestrictions.RestrictAsync(db, scope.ServiceProvider.GetRequiredService<IClock>(), subject.PartyId, d.Class, d.Count, reason, legalBasis, retentionEnd, ct).ConfigureAwait(false));
        }

        return new DsarModuleResult(Module, outcomes);
    }

    private static async Task<List<(string Class, int Count, object Rows)>> CollectAsync(MigrationDbContext db, SubjectRef subject, CancellationToken ct)
    {
        var core = subject.PartyId.Value;
        var xrefs = await db.Xrefs.AsNoTracking().Where(x => x.CoreId == core).OrderBy(x => x.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        var items = await db.Items.AsNoTracking().Where(i => i.CoreId == core).OrderBy(i => i.BatchId).ThenBy(i => i.Seq).ToListAsync(ct).ConfigureAwait(false);
        return
        [
            (Xref, xrefs.Count, xrefs.Select(x => new { id = x.XrefId, source = x.Source, objectType = x.ObjectType, legacyKey = x.LegacyKey, origin = x.Origin, status = x.Status, batchId = x.BatchId }).ToList()),
            (Archive, items.Count, items.Select(i => new { id = i.ItemId, batchId = i.BatchId, module = i.ModuleName, objectType = i.ObjectType, legacyId = i.LegacyId, outcome = i.Outcome, payload = i.Payload }).ToList()),
        ];
    }
}
