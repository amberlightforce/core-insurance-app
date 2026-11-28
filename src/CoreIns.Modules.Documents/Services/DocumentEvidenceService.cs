using CoreIns.Modules.Documents.Commands;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Documents.Services;

/// <summary>The in-process <see cref="IDocumentEvidenceService"/>: the evidence-pack export command and the completeness report for another module (BO-CMP-09).</summary>
internal sealed class DocumentEvidenceService(
    ICommandHandler<ExportEvidencePack, EvidencePackExportResponse> exporter, ArchiveCompleteness completeness, RequestContext context) : IDocumentEvidenceService
{
    public async Task<Result<EvidencePackExportResponse>> ExportPackAsync(EvidencePackExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = CommandOptions.New();
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return await exporter.HandleAsync(new ExportEvidencePack(request), cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<ArchiveCompletenessResponse> CompletenessAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken = default) =>
        completeness.ReportAsync(firstDay, lastDay, cancellationToken);
}
