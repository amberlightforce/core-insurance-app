using CoreIns.Modules.Documents.Domain;
using CoreIns.Modules.Documents.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Documents.Queries;

/// <summary>Which template version a document request wants: the type, an optional scope and the valid date.</summary>
internal sealed record TemplateScope(string DocumentType, DateOnly ValidAt, string? ProductCode = null, string? LineOfBusiness = null, string? Jurisdiction = null);

/// <summary>Reads the persisted document type, template and clause catalogue (BO-DOC-05). The catalogue is global, not per legal entity.</summary>
internal sealed class TemplateCatalogue(DocumentsDbContext db)
{
    /// <summary>
    /// The APPROVED version in force at the date. A template scope column that is null is a wildcard; a set one must equal the
    /// requested value. The most specific match wins, then the highest version. A DRAFT version is never returned.
    /// </summary>
    public async Task<TemplateRow?> ResolveAsync(TemplateScope scope, CancellationToken cancellationToken)
    {
        var candidates = await db.Templates.AsNoTracking()
            .Where(t => t.DocumentType == scope.DocumentType && t.Status == "APPROVED" && t.ValidFrom <= scope.ValidAt && (t.ValidTo == null || t.ValidTo >= scope.ValidAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return candidates
            .Where(t => Matches(t.ProductCode, scope.ProductCode) && Matches(t.LineOfBusiness, scope.LineOfBusiness) && Matches(t.Jurisdiction, scope.Jurisdiction))
            .OrderByDescending(t => Specificity(t))
            .ThenByDescending(t => t.Version)
            .FirstOrDefault();
    }

    /// <summary>The resolved template joined with its document type, ready to render.</summary>
    public async Task<LetterTemplate?> ResolveLetterAsync(TemplateScope scope, CancellationToken cancellationToken)
    {
        var template = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);
        if (template is null)
        {
            return null;
        }

        var type = await db.DocumentTypes.AsNoTracking().SingleOrDefaultAsync(d => d.DocumentType == template.DocumentType, cancellationToken).ConfigureAwait(false);
        return type is null
            ? null
            : new LetterTemplate(template.DocumentType, template.Version, type.DefaultMedium, type.RequiredFields, template.TitleEl, template.BodyEl, template.TitleEn, template.BodyEn);
    }

    public async Task<IReadOnlyList<DocumentTypeRow>> ListTypesAsync(CancellationToken cancellationToken) =>
        await db.DocumentTypes.AsNoTracking().OrderBy(d => d.DocumentType).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<TemplateRow>> ListTemplatesAsync(string? documentType, string? status, CancellationToken cancellationToken)
    {
        var query = db.Templates.AsNoTracking();
        if (!string.IsNullOrEmpty(documentType))
        {
            query = query.Where(t => t.DocumentType == documentType);
        }

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(t => t.Status == status);
        }

        return await query.OrderBy(t => t.DocumentType).ThenBy(t => t.Version).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A clause by id or by its code.</summary>
    public async Task<ClauseRow?> GetClauseAsync(string idOrCode, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrCode, out var id))
        {
            return await db.Clauses.AsNoTracking().SingleOrDefaultAsync(c => c.ClauseId == id, cancellationToken).ConfigureAwait(false);
        }

        return await db.Clauses.AsNoTracking().SingleOrDefaultAsync(c => c.Code == idOrCode, cancellationToken).ConfigureAwait(false);
    }

    private static bool Matches(string? templateValue, string? requested) => templateValue is null || string.Equals(templateValue, requested, StringComparison.Ordinal);

    private static int Specificity(TemplateRow t) => (t.ProductCode is null ? 0 : 1) + (t.LineOfBusiness is null ? 0 : 1) + (t.Jurisdiction is null ? 0 : 1);
}
