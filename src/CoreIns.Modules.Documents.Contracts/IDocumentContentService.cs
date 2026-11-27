using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Documents.Contracts;

/// <summary>
/// Synchronous in-process SPI to read the archived bytes of one outbound document (BO-CHN-04: the customer portal downloads through CHN, which
/// checks ownership first; DOC's own <c>doc.Archive.download</c> is staff only). The service does no ownership check of its own: the caller
/// compares <see cref="DocumentContent.RecipientPartyId"/> with the customer's scope and refuses everything else.
/// </summary>
public interface IDocumentContentService
{
    /// <summary>The document's bytes, or null when the document does not exist in the caller's legal entity.</summary>
    Task<DocumentContent?> ReadAsync(DocumentId documentId, CancellationToken cancellationToken = default);
}

/// <summary>One document's archived bytes.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="DocumentNumber">Its number.</param>
/// <param name="RecipientPartyId">The addressee.</param>
/// <param name="ContentType">Media type of the archived bytes.</param>
/// <param name="Content">The bytes.</param>
/// <param name="Hash">SHA-256 of the bytes as stored.</param>
public sealed record DocumentContent(DocumentId DocumentId, string DocumentNumber, PartyId RecipientPartyId, string ContentType, byte[] Content, string Hash);
