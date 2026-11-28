using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Modules.Documents.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Documents.Services;

/// <summary>The in-process <see cref="IDocumentArchiveService"/>: stores and reads archive entries of the caller's legal entity (BO-WRK-11).</summary>
internal sealed class DocumentArchiveService(DocumentsDbContext db, DocumentReader reader, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : IDocumentArchiveService
{
    private const int MaxBytes = 5 * 1024 * 1024;

    public async Task<ArchivedBlob> StoreAsync(byte[] content, string contentType, string metadataJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content), "An archive entry holds 1 byte to 5 MB.");
        }

        var hash = Sha256Hash.Compute(content);
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var archive = new ArchiveRow
        {
            ArchiveId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            ContentType = contentType,
            Content = content,
            ContentHash = hash.Value,
            Metadata = string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson,
            CreatedAt = clock.Now,
        };
        db.Archives.Add(archive);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ArchivedBlob(archive.ArchiveId, hash.Value);
    }

    public async Task<ArchivedContent?> ReadAsync(Guid archiveId, CancellationToken cancellationToken = default) =>
        await reader.DownloadAsync(archiveId, cancellationToken).ConfigureAwait(false) is { } found
            ? new ArchivedContent(archiveId, found.ContentType, found.Content, found.Hash)
            : null;
}
