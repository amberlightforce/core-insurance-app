using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Documents.Contracts;

/// <summary>
/// Synchronous in-process SPI of the archive evidence operations other modules need (BO-CMP-09: the regulatory submission tracker builds a
/// submission bundle and shows archive gaps). It runs the same code as <c>doc.EvidencePack.export</c> and <c>doc.Archive.completeness</c> in the caller's
/// legal entity; callers never read the DOC schema.
/// </summary>
public interface IDocumentEvidenceService
{
    /// <summary>Exports an evidence pack (a deterministic zip stored as an archive entry). DSAR-restricted recipients are excluded unless a legal-claim reason is given.</summary>
    Task<Result<EvidencePackExportResponse>> ExportPackAsync(EvidencePackExportRequest request, CancellationToken cancellationToken = default);

    /// <summary>The archive completeness report of a date range (gaps per UTC month).</summary>
    Task<ArchiveCompletenessResponse> CompletenessAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken = default);
}
