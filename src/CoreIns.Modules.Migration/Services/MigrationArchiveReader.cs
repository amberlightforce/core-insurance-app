using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Modules.Migration.Services.Dsar;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services;

/// <summary>A masked archive record: the legacy key is partly hidden and the payload is never returned, only its field names.</summary>
internal sealed record ArchiveRecordView(
    Guid Id, Guid BatchId, string Module, string ObjectType, string LegacyKeyMasked, string Outcome, Guid? CoreId, bool Restricted, IReadOnlyList<string> PayloadFields);

internal sealed record ArchiveRecordPage(IReadOnlyList<ArchiveRecordView> Items, string? NextCursor, int Limit);

internal sealed record ArchiveRecordsResponse(IReadOnlyList<ArchiveRecordView> Records);

/// <summary>A master mark of one object in the unified enquiry.</summary>
internal sealed record EnquiryMark(string ObjectType, string Key, string Master, string? ValidFrom, string? ValidTo, Guid? CoreId, string? Source);

internal sealed record EnquiryFreshness(string AsOf, string? LastMigratedAt);

internal sealed record EnquiryResponse(IReadOnlyList<EnquiryMark> SummaryMasterMarks, EnquiryFreshness Freshness);

/// <summary>Read side of the migration archive (masked) and of the unified cross-system enquiry.</summary>
internal sealed class MigrationArchiveReader(MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, MigrationReader reader, IClock clock)
{
    private const int MaxKeys = 50;
    private const int MaxKeyLength = 200;

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    /// <summary>Hides the middle of a key; short keys are hidden entirely.</summary>
    internal static string Mask(string key) =>
        key.Length <= 6 ? new string('*', key.Length) : string.Concat(key.AsSpan(0, 2), new string('*', key.Length - 4), key.AsSpan(key.Length - 2));

    private static IReadOnlyList<string> Fields(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? [.. doc.RootElement.EnumerateObject().Select(p => p.Name).Take(50)] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<List<ArchiveRecordView>> ViewsAsync(List<BatchItemRow> rows, CancellationToken cancellationToken)
    {
        var cores = rows.Where(r => r.CoreId is not null).Select(r => r.CoreId!.Value).Distinct().ToList();
        var restricted = cores.Count == 0
            ? []
            : await db.Set<DsarRestrictionRow>().AsNoTracking()
                .Where(r => cores.Contains(r.PartyId) && r.RecordClass == MigrationDsarParticipant.Archive)
                .Select(r => r.PartyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new ArchiveRecordView(
            r.ItemId, r.BatchId, r.ModuleName, r.ObjectType, Mask(r.LegacyId), r.Outcome, r.CoreId, r.CoreId is { } c && restricted.Contains(c), Fields(r.Payload)))];
    }

    /// <summary>Searches migrated items by legacy key or core id. Keys are required (comma separated): the archive is never listed whole.</summary>
    public async Task<Result<ArchiveRecordPage>> SearchAsync(string? keys, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var list = (keys ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count is 0 or > MaxKeys || list.Any(k => k.Length > MaxKeyLength))
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, $"Give between 1 and {MaxKeys} keys.");
        }

        var ids = list.Select(k => Guid.TryParse(k, out var g) ? g : (Guid?)null).Where(g => g is not null).Select(g => g!.Value).ToList();
        var legalEntity = LegalEntity;
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var offset = BatchCursor.Decode(cursor);
        var batchIds = db.Batches.AsNoTracking().Where(b => b.LegalEntityId == legalEntity).Select(b => b.BatchId);
        var rows = await db.Items.AsNoTracking()
            .Where(i => batchIds.Contains(i.BatchId) && (list.Contains(i.LegacyId) || (i.CoreId != null && ids.Contains(i.CoreId.Value))))
            .OrderBy(i => i.BatchId).ThenBy(i => i.Seq).Skip(offset).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        return new ArchiveRecordPage(await ViewsAsync(page, cancellationToken).ConfigureAwait(false), rows.Count > size ? (offset + size).ToString(CultureInfo.InvariantCulture) : null, size);
    }

    public async Task<Result<ArchiveRecordsResponse>> GetAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var batchIds = db.Batches.AsNoTracking().Where(b => b.LegalEntityId == legalEntity).Select(b => b.BatchId);
        var row = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.ItemId == itemId && batchIds.Contains(i.BatchId), cancellationToken).ConfigureAwait(false);
        return row is null
            ? MigrationErrors.NotFound("The archive record")
            : new ArchiveRecordsResponse(await ViewsAsync([row], cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Unified summary of one party, policy or claim: who is master of each object today and how fresh the migrated data is.</summary>
    public async Task<Result<EnquiryResponse>> EnquireAsync(string? party, string? policyKey, string? claimKey, CancellationToken cancellationToken)
    {
        var requested = new List<(string ObjectType, string Key)>();
        foreach (var (type, key) in new[] { ("Party", party), ("Policy", policyKey), ("Claim", claimKey) })
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (key.Length > MaxKeyLength)
            {
                return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "A key is too long.");
            }

            requested.Add((type, key.Trim()));
        }

        if (requested.Count == 0)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "Give a party, policyKey or claimKey.");
        }

        var legalEntity = LegalEntity;
        var now = clock.Now.ToDateTimeOffset();
        var today = new BusinessDate(DateOnly.FromDateTime(now.UtcDateTime));
        var marks = new List<EnquiryMark>();
        Instant? last = null;
        foreach (var (type, key) in requested)
        {
            var route = (await reader.ResolveRouteAsync(type, key, today, cancellationToken).ConfigureAwait(false)).Value;
            var asGuid = Guid.TryParse(key, out var parsedKey) ? parsedKey : Guid.Empty;
            var xref = await db.Xrefs.AsNoTracking()
                .Where(x => x.LegalEntityId == legalEntity && x.ObjectType == type && x.Status == "ACTIVE" && (x.LegacyKey == key || x.CoreId == asGuid))
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (xref is not null && (last is null || xref.CreatedAt.ToDateTimeOffset() > last.Value.ToDateTimeOffset()))
            {
                last = xref.CreatedAt;
            }

            marks.Add(new EnquiryMark(type, key, route.MasterState, route.ValidFrom, route.ValidTo, xref?.CoreId, xref?.Source));
        }

        return new EnquiryResponse(marks, new EnquiryFreshness(now.ToString("O", CultureInfo.InvariantCulture), last?.ToDateTimeOffset().ToString("O", CultureInfo.InvariantCulture)));
    }
}
