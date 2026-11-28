using System.Text;
using System.Text.Json;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Modules.Documents.Rendering;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Documents.Commands;

/// <summary>
/// <c>doc.Document.renderPdf</c> (BO-DOC-07): renders the archived HTML letter of a document to PDF and stores it as a <c>PDF</c>
/// rendition next to the HTML. The HTML archive entry, the delivery and the document row are not touched, so how real sends
/// render is unchanged.
/// </summary>
internal sealed record RenderDocumentPdf(DocumentId DocumentId) : ICommand<DocumentRenderPdfResponse>;

internal sealed class RenderDocumentPdfHandler(
    DocumentsDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IPdfRenderer renderer,
    IPdfPostProcessor postProcessor) : ICommandHandler<RenderDocumentPdf, DocumentRenderPdfResponse>
{
    internal const string RenditionKind = "PDF";

    public async Task<Result<DocumentRenderPdfResponse>> HandleAsync(RenderDocumentPdf command, CancellationToken cancellationToken)
    {
        var entity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var documentId = command.DocumentId;
        var document = await db.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.DocumentId == documentId && d.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "The document does not exist in your legal entity.");
        }

        var existing = await db.Archives.AsNoTracking()
            .Where(a => a.LegalEntityId == entity && a.ParentArchiveId == document.ArchiveId && a.RenditionKind == RenditionKind)
            .OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new DocumentRenderPdfResponse
            {
                DocumentId = command.DocumentId,
                ArchiveId = existing.ArchiveId,
                Hash = Sha256Hash.Parse(existing.ContentHash),
                PdfA = ReadPdfA(existing.Metadata),
                AlreadyRendered = true,
            };
        }

        var source = await db.Archives.AsNoTracking()
            .Where(a => a.ArchiveId == document.ArchiveId && a.LegalEntityId == entity)
            .Select(a => new { a.Content, a.ContentHash }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return DomainError.Of(ModuleCode.DOC, "NOT-FOUND", "The archived content of the document is missing.");
        }

        var html = Encoding.UTF8.GetString(source.Content);
        PdfRenderResult rendered;
        try
        {
            rendered = await renderer.RenderAsync(html, cancellationToken).ConfigureAwait(false);
        }
        catch (PdfRenderException ex)
        {
            return DomainError.Of(ModuleCode.DOC, "RENDER-FAILED", ex.Message);
        }

        var bytes = postProcessor.Process(rendered.Bytes, new PdfPostProcessContext(html, source.ContentHash, rendered.PdfA));
        var hash = Sha256Hash.Compute(bytes);
        var archive = new ArchiveRow
        {
            ArchiveId = Guid.CreateVersion7(),
            LegalEntityId = entity,
            ContentType = "application/pdf",
            Content = bytes,
            ContentHash = hash.Value,
            Metadata = JsonSerializer.Serialize(new { documentId = command.DocumentId, pdfa = rendered.PdfA, renderer = rendered.Renderer, sourceHash = source.ContentHash }),
            CreatedAt = clock.Now,
            ParentArchiveId = document.ArchiveId,
            RenditionKind = RenditionKind,
        };
        var parentRetention = await db.Archives.AsNoTracking().Where(a => a.ArchiveId == document.ArchiveId).Select(a => a.RetentionClass)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        archive.RetentionClass = parentRetention;
        db.Archives.Add(archive);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentRenderPdfResponse
        {
            DocumentId = command.DocumentId,
            ArchiveId = archive.ArchiveId,
            Hash = hash,
            PdfA = rendered.PdfA,
            AlreadyRendered = false,
        };
    }

    private static bool ReadPdfA(string metadata)
    {
        try
        {
            using var json = JsonDocument.Parse(metadata);
            return json.RootElement.TryGetProperty("pdfa", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
