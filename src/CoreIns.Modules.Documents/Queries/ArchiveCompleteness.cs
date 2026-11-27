using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Documents.Commands;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Documents.Queries;

/// <summary>
/// Archive completeness report (BO-DOC-04). Every issued document (Rendered or Superseded) of the legal entity created in the range must
/// have an archived artefact whose recomputed SHA-256 equals the archive's stored hash and the document's hash. Gaps and orphan archive
/// entries are reported per UTC creation month; nothing is changed.
/// </summary>
internal sealed class ArchiveCompleteness(DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IOptions<DocumentsOptions> options)
{
    private const int BatchSize = 25;
    private const int OrphanScanCap = 5000;
    private static readonly string[] Issued = ["Rendered", "Superseded"];

    public async Task<ArchiveCompletenessResponse> ReportAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var start = Instant.FromUtcDateTime(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var end = Instant.FromUtcDateTime(to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var max = options.Value.CompletenessMaxDocuments;

        var documents = await db.Documents.AsNoTracking()
            .Where(d => d.LegalEntityId == entity && d.CreatedAt >= start && d.CreatedAt < end && Issued.Contains(d.Status))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.DocumentNumber).Take(max + 1)
            .Select(d => new { d.DocumentId, d.DocumentNumber, d.ArchiveId, d.ContentHash, d.CreatedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var truncated = documents.Count > max;
        if (truncated)
        {
            documents.RemoveAt(documents.Count - 1);
        }

        var periods = new SortedDictionary<string, (int Issued, int Ok, List<ArchiveCompletenessGap> Gaps, List<Guid> Orphans)>(StringComparer.Ordinal);
        (int Issued, int Ok, List<ArchiveCompletenessGap> Gaps, List<Guid> Orphans) Bucket(DateTimeOffset at)
        {
            var key = at.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (!periods.TryGetValue(key, out var bucket))
            {
                bucket = (0, 0, [], []);
            }

            return bucket;
        }

        void Store(DateTimeOffset at, (int Issued, int Ok, List<ArchiveCompletenessGap> Gaps, List<Guid> Orphans) bucket) =>
            periods[at.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)] = bucket;

        foreach (var batch in documents.Chunk(BatchSize))
        {
            var ids = batch.Select(d => d.ArchiveId).ToList();
            var archives = (await db.Archives.AsNoTracking().Where(a => a.LegalEntityId == entity && ids.Contains(a.ArchiveId))
                .Select(a => new { a.ArchiveId, a.ContentHash, a.Content }).ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(a => a.ArchiveId);
            foreach (var document in batch)
            {
                var created = document.CreatedAt.ToDateTimeOffset();
                var bucket = Bucket(created);
                bucket.Issued++;
                if (!archives.TryGetValue(document.ArchiveId, out var archive))
                {
                    bucket.Gaps.Add(Gap(document.DocumentId, document.DocumentNumber, document.ArchiveId, ArchiveCompletenessGap.KindValue.MissingArchive));
                }
                else if (Sha256Hash.Compute(archive.Content).Value != archive.ContentHash)
                {
                    bucket.Gaps.Add(Gap(document.DocumentId, document.DocumentNumber, document.ArchiveId, ArchiveCompletenessGap.KindValue.HashMismatch));
                }
                else if (archive.ContentHash != document.ContentHash)
                {
                    bucket.Gaps.Add(Gap(document.DocumentId, document.DocumentNumber, document.ArchiveId, ArchiveCompletenessGap.KindValue.DocumentHashDiffers));
                }
                else
                {
                    bucket.Ok++;
                }

                Store(created, bucket);
            }
        }

        // Orphans: archive entries created in the range that no document and no rendition refers to. Evidence packs are exports, not orphans.
        var candidates = await db.Archives.AsNoTracking()
            .Where(a => a.LegalEntityId == entity && a.CreatedAt >= start && a.CreatedAt < end && a.ParentArchiveId == null)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.ArchiveId).Take(OrphanScanCap)
            .Select(a => new { a.ArchiveId, a.CreatedAt, a.Metadata }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in candidates.Chunk(500))
        {
            var ids = chunk.Select(c => c.ArchiveId).ToList();
            var referenced = (await db.Documents.AsNoTracking().Where(d => d.LegalEntityId == entity && ids.Contains(d.ArchiveId)).Select(d => d.ArchiveId)
                .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            foreach (var candidate in chunk.Where(c => !referenced.Contains(c.ArchiveId) && !IsEvidencePack(c.Metadata)))
            {
                var created = candidate.CreatedAt.ToDateTimeOffset();
                var bucket = Bucket(created);
                bucket.Orphans.Add(candidate.ArchiveId);
                Store(created, bucket);
            }
        }

        return new ArchiveCompletenessResponse
        {
            From = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Periods = [.. periods.Select(p => new ArchiveCompletenessPeriod { Period = p.Key, Issued = p.Value.Issued, Ok = p.Value.Ok, Gaps = p.Value.Gaps, OrphanArchiveIds = p.Value.Orphans })],
            Truncated = truncated,
        };
    }

    private static ArchiveCompletenessGap Gap(DocumentId documentId, string number, Guid archiveId, ArchiveCompletenessGap.KindValue kind) =>
        new() { DocumentId = documentId, DocumentNumber = number, ArchiveId = archiveId, Kind = kind };

    private static bool IsEvidencePack(string metadata)
    {
        try
        {
            using var json = JsonDocument.Parse(metadata);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == ExportEvidencePackHandler.Kind;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
