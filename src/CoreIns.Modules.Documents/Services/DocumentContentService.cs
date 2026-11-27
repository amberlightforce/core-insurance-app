using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Queries;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Documents.Services;

/// <summary>The in-process <see cref="IDocumentContentService"/>: the stored bytes of a document of the caller's legal entity (BO-CHN-04).</summary>
internal sealed class DocumentContentService(DocumentReader reader) : IDocumentContentService
{
    public async Task<DocumentContent?> ReadAsync(DocumentId documentId, CancellationToken cancellationToken = default)
    {
        if (await reader.GetAsync(documentId, cancellationToken).ConfigureAwait(false) is not { } view
            || await reader.DownloadAsync(view.ArchiveId, cancellationToken).ConfigureAwait(false) is not { } bytes)
        {
            return null;
        }

        return new DocumentContent(documentId, view.DocumentNumber, new PartyId(view.RecipientPartyId), bytes.ContentType, bytes.Content, bytes.Hash);
    }
}
