namespace CoreIns.Modules.Documents.Contracts;

/// <summary>
/// Synchronous in-process SPI to put a blob into the document archive and read it back (BO-WRK-11: inbound documents are stored through
/// <c>archives/store</c>). It runs the same storage as <c>doc.Archive.store</c> in the caller's legal entity; callers never read the DOC schema.
/// </summary>
public interface IDocumentArchiveService
{
    /// <summary>Stores <paramref name="content"/> (at most 5 MB) as an archive entry with the given media type and metadata JSON object.</summary>
    Task<ArchivedBlob> StoreAsync(byte[] content, string contentType, string metadataJson, CancellationToken cancellationToken = default);

    /// <summary>The bytes of an archive entry of the caller's legal entity, or null.</summary>
    Task<ArchivedContent?> ReadAsync(Guid archiveId, CancellationToken cancellationToken = default);
}

/// <summary>A stored archive entry.</summary>
/// <param name="ArchiveId">Archive id.</param>
/// <param name="Hash">SHA-256 of the bytes.</param>
public sealed record ArchivedBlob(Guid ArchiveId, string Hash);

/// <summary>The bytes of an archive entry.</summary>
/// <param name="ArchiveId">Archive id.</param>
/// <param name="ContentType">Media type.</param>
/// <param name="Content">The bytes.</param>
/// <param name="Hash">SHA-256 as stored.</param>
public sealed record ArchivedContent(Guid ArchiveId, string ContentType, byte[] Content, string Hash);
