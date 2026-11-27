using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Documents.Api;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Documents.Commands;

/// <summary>
/// <c>doc.EvidencePack.export</c> (BO-DOC-04): a deterministic zip of the artefacts of a subject, a recipient or a period with a hash manifest
/// and the delivery evidence, stored as an archive entry (content type <c>application/zip</c>, downloadable via <c>doc.Archive.download</c>).
/// </summary>
internal sealed record ExportEvidencePack(EvidencePackExportRequest Request) : ICommand<EvidencePackExportResponse>;

internal sealed class ExportEvidencePackHandler(
    DocumentsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IOptions<DocumentsOptions> options)
    : ICommandHandler<ExportEvidencePack, EvidencePackExportResponse>
{
    internal const string Kind = "EVIDENCE_PACK";
    private static readonly DateTimeOffset FixedStamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<Result<EvidencePackExportResponse>> HandleAsync(ExportEvidencePack command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        ObjectRef? subject = null;
        if (request.Subject is { Length: > 0 } text && !DocumentsController.TryParseObjectRef(text, out subject))
        {
            return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "subject must be MODULE/Type/id.");
        }

        DateOnly? createdFrom = null;
        DateOnly? createdTo = null;
        if ((request.CreatedFrom is { Length: > 0 } f && !TryDate(f, out createdFrom)) || (request.CreatedTo is { Length: > 0 } t && !TryDate(t, out createdTo)))
        {
            return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "createdFrom and createdTo must be dates (yyyy-MM-dd).");
        }

        if (request.PackId is null && subject is null && request.PartyId is null && createdFrom is null && createdTo is null)
        {
            return DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "Give packId, subject, partyId or a creation date range.");
        }

        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var query = db.Documents.AsNoTracking().Where(d => d.LegalEntityId == entity);
        if (request.PackId is { } packId)
        {
            var id = new DocumentId(packId);
            query = query.Where(d => d.DocumentId == id);
        }

        if (subject is not null)
        {
            var module = subject.Module.ToString();
            query = query.Where(d => d.SubjectModule == module && d.SubjectType == subject.Type && d.SubjectId == subject.Id);
        }

        if (request.PartyId is { } party)
        {
            query = query.Where(d => d.RecipientPartyId == party);
        }

        if (createdFrom is { } from)
        {
            var start = Instant.FromUtcDateTime(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            query = query.Where(d => d.CreatedAt >= start);
        }

        if (createdTo is { } to)
        {
            var end = Instant.FromUtcDateTime(to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            query = query.Where(d => d.CreatedAt < end);
        }

        var max = options.Value.EvidencePackMaxDocuments;
        var documents = await query.OrderBy(d => d.CreatedAt).ThenBy(d => d.DocumentNumber).Take(max + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (documents.Count == 0)
        {
            return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "No document matches the scope in your legal entity.");
        }

        if (documents.Count > max)
        {
            return DomainError.Of(ModuleCode.DOC, "PACK-TOO-LARGE", $"The scope holds more than {max} documents; narrow it.");
        }

        // A recipient under a DSAR restriction is processed only for legal claims (GDPR Art. 18(2)): without a stated reason the
        // documents stay out of the pack and the manifest records the exclusion, so the pack never widens a restriction.
        var recipients = documents.Select(d => d.RecipientPartyId.Value).Distinct().ToList();
        var restricted = (await db.Set<DsarRestrictionRow>().AsNoTracking().Where(r => recipients.Contains(r.PartyId)).Select(r => r.PartyId).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var legalClaim = !string.IsNullOrWhiteSpace(request.LegalClaimReason);

        var documentIds = documents.Select(d => d.DocumentId).ToList();
        var deliveries = await db.Deliveries.AsNoTracking().Where(d => documentIds.Contains(d.DocumentId)).OrderBy(d => d.CreatedAt).ThenBy(d => d.DeliveryId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var evidence = await db.Evidence.AsNoTracking().Where(e => documentIds.Contains(e.DocumentId)).OrderBy(e => e.ReceivedAt).ThenBy(e => e.EvidenceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var manifestDocuments = new List<object>();
        var excluded = 0;
        foreach (var document in documents)
        {
            if (restricted.Contains(document.RecipientPartyId.Value) && !legalClaim)
            {
                excluded++;
                manifestDocuments.Add(new
                {
                    documentId = document.DocumentId.Value, documentNumber = document.DocumentNumber, contentHash = document.ContentHash,
                    included = false, excludedReason = "DSAR_RESTRICTED", artefacts = Array.Empty<object>(),
                });
                continue;
            }

            var folder = $"documents/{document.DocumentNumber}/";
            files[folder + "document.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                documentId = document.DocumentId.Value, documentNumber = document.DocumentNumber, documentType = document.TemplateKey,
                templateVersion = document.TemplateVersion, requestNo = document.RequestNo,
                subject = $"{document.SubjectModule}/{document.SubjectType}/{document.SubjectId}", recipientPartyId = document.RecipientPartyId.Value,
                language = document.Language, status = document.Status, medium = document.Medium, archiveId = document.ArchiveId,
                contentHash = document.ContentHash, createdAt = document.CreatedAt.ToDateTimeOffset(),
                deliveries = deliveries.Where(d => d.DocumentId == document.DocumentId).Select(d => new
                {
                    deliveryId = d.DeliveryId.Value, channel = d.Channel, medium = d.Medium, status = d.Status, proofLevel = d.ProofLevel,
                    notificationDate = d.NotificationDate?.Value, failureReason = d.FailureReason, isReprint = d.IsReprint,
                }),
            }, Json);
            files[folder + "evidence.json"] = JsonSerializer.SerializeToUtf8Bytes(
                evidence.Where(e => e.DocumentId == document.DocumentId).Select(e => new
                {
                    evidenceId = e.EvidenceId, deliveryId = e.DeliveryId.Value, provider = e.Provider, providerEventId = e.ProviderEventId, eventType = e.EventType,
                    proofDate = e.ProofDate?.Value, proofReference = e.ProofReference, outcome = e.Outcome, receivedAt = e.ReceivedAt.ToDateTimeOffset(),
                }), Json);

            var artefacts = new List<object>();
            var archives = await db.Archives.AsNoTracking()
                .Where(a => a.LegalEntityId == entity && (a.ArchiveId == document.ArchiveId || a.ParentArchiveId == document.ArchiveId))
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.ArchiveId).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (archives.All(a => a.ArchiveId != document.ArchiveId))
            {
                artefacts.Add(new { archiveId = document.ArchiveId, present = false });
            }

            foreach (var archive in archives)
            {
                var computed = Sha256Hash.Compute(archive.Content).Value;
                var path = $"{folder}artefacts/{archive.ArchiveId:N}.{Extension(archive.ContentType)}";
                files[path] = archive.Content;
                artefacts.Add(new
                {
                    archiveId = archive.ArchiveId, present = true, path, contentType = archive.ContentType, renditionKind = archive.RenditionKind,
                    sha256 = computed, storedSha256 = archive.ContentHash, verified = computed == archive.ContentHash && (archive.ArchiveId != document.ArchiveId || computed == document.ContentHash),
                });
            }

            manifestDocuments.Add(new
            {
                documentId = document.DocumentId.Value, documentNumber = document.DocumentNumber, contentHash = document.ContentHash,
                included = true, excludedReason = (string?)null, artefacts,
            });
        }

        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            scope = new { packId = request.PackId, subject = request.Subject, partyId = request.PartyId, createdFrom = request.CreatedFrom, createdTo = request.CreatedTo },
            documents = manifestDocuments,
            files = files.Select(f => new { path = f.Key, sha256 = Sha256Hash.Compute(f.Value).Value, bytes = f.Value.Length }),
        }, Json);

        var zip = BuildZip(files, manifest);
        var hash = Sha256Hash.Compute(zip);
        var pack = new ArchiveRow
        {
            ArchiveId = Guid.CreateVersion7(),
            LegalEntityId = entity,
            ContentType = "application/zip",
            Content = zip,
            ContentHash = hash.Value,
            Metadata = JsonSerializer.Serialize(new
            {
                kind = Kind, documentCount = documents.Count, excludedCount = excluded, legalClaimReason = legalClaim ? request.LegalClaimReason!.Trim() : null,
            }, Json),
            CreatedAt = clock.Now,
        };
        db.Archives.Add(pack);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new EvidencePackExportResponse
        {
            ArchiveId = pack.ArchiveId, Hash = hash, DocumentCount = documents.Count, ExcludedCount = excluded, EntryCount = files.Count + 1,
        };
    }

    private static bool TryDate(string text, out DateOnly? date)
    {
        var ok = DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed);
        date = ok ? parsed : null;
        return ok;
    }

    // Deterministic: sorted entries, a fixed timestamp and no compression, so equal input gives equal bytes across runtimes.
    private static byte[] BuildZip(SortedDictionary<string, byte[]> files, byte[] manifest)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
        {
            Add(zip, "manifest.json", manifest);
            foreach (var (path, content) in files)
            {
                Add(zip, path, content);
            }
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string path, byte[] content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = FixedStamp;
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string Extension(string contentType) => contentType.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "text/html" => "html",
        "application/pdf" => "pdf",
        "application/zip" => "zip",
        _ => "bin",
    };
}
