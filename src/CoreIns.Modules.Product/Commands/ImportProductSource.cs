using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Domain;
using CoreIns.Modules.Product.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Product.Commands;

/// <summary>
/// <c>pfc.ProductImport.import</c> (BO-PFC-03, REQ-PFC-240): reads a complete product definition from YAML or JSON text into a draft change set on an
/// existing product. Unlike <c>ProductVersion.import</c> it never creates a Locked version, and lint findings do not block the draft (they block submitting
/// it, BO-PFC-04). With dry run nothing is stored and the findings are returned.
/// </summary>
internal sealed record ImportProductSource(ProductImportImportRequest Request) : ICommand<ProductImportImportResponse>;

internal sealed class ImportProductSourceValidator : AbstractValidator<ImportProductSource>
{
    public ImportProductSourceValidator() =>
        RuleFor(c => c.Request.Source).NotEmpty().MaximumLength(2_000_000).WithErrorCode("SOURCE");
}

internal sealed class ImportProductSourceHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities) : ICommandHandler<ImportProductSource, ProductImportImportResponse>
{
    public async Task<Result<ProductImportImportResponse>> HandleAsync(ImportProductSource command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var parsed = ParseSource(request.Source, request.Format);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var definition = parsed.Value;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        if (!string.Equals(definition.LegalEntity, caller.Value, StringComparison.Ordinal))
        {
            return Invalid("legalEntity", "The definition belongs to another legal entity than the caller's.");
        }

        var legalEntity = legalEntities.Resolve(caller);
        var product = await db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.Code == definition.Product.Code, cancellationToken).ConfigureAwait(false);
        if (product is null)
        {
            // TODO(hardening): a definition of a product that does not exist yet needs a change set without a base version; today the first version goes through ProductVersion.import.
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-PRODUCT", $"Product '{definition.Product.Code}' is unknown for this legal entity and jurisdiction.");
        }

        if (!string.Equals(product.Jurisdiction, definition.Jurisdiction, StringComparison.Ordinal))
        {
            return Invalid("jurisdiction", "The product exists in another jurisdiction.");
        }

        var locked = db.Versions.AsNoTracking().Where(v => v.ProductId == product.ProductId && v.Status == "LOCKED");
        if (request.BaseVersion is { } requested)
        {
            var major = requested.Major;
            var minor = requested.Minor;
            locked = locked.Where(v => v.Major == major && v.Minor == minor);
        }

        var baseVersion = await locked.OrderByDescending(v => v.Major).ThenByDescending(v => v.Minor).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (baseVersion is null)
        {
            return DomainError.Of(ModuleCode.PFC, "NO-VERSION", $"There is no Locked version of '{product.Code}' to base the draft on.");
        }

        var validation = ChangeSetSupport.Validation(definition);
        if (context.DryRun)
        {
            return new ProductImportImportResponse { Created = false, Validation = validation };
        }

        var now = clock.Now;
        var actor = context.Actor.ToString();
        var row = db.ChangeSets.Add(new ChangeSetRow
        {
            ChangeSetId = EntityIds.NewGuid(),
            LegalEntityId = legalEntity,
            Jurisdiction = product.Jurisdiction,
            ProductId = product.ProductId,
            BaseVersionId = baseVersion.ProductVersionId,
            Status = ChangeSetSupport.Open,
            DraftJson = CanonicalDraft(definition),
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor,
        }).Entity;
        if (await ChangeSetSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        var view = await ChangeSetSupport.ViewAsync(db, row, product.Code, cancellationToken).ConfigureAwait(false);
        if (view.IsFailure)
        {
            return view.Error;
        }

        return new ProductImportImportResponse { Created = true, ChangeSet = view.Value, Validation = view.Value.Validation };
    }

    /// <summary>YAML or JSON text to the typed definition; any shape error is PFC-ERR-IMPORT-INVALID.</summary>
    internal static Result<ProductArtefact> ParseSource(string source, ProductImportImportRequest.FormatValue? format)
    {
        try
        {
            var yaml = format switch
            {
                ProductImportImportRequest.FormatValue.Yaml => true,
                ProductImportImportRequest.FormatValue.Json => false,
                _ => !source.TrimStart().StartsWith('{'),
            };
            var tree = yaml ? ProductYaml.Read(source) : JsonNode.Parse(source);
            if (tree is not JsonObject)
            {
                return ImportInvalid("The source must be one product definition (a mapping).");
            }

            return ArtefactCompiler.Parse(tree.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return ImportInvalid(ex.Message);
        }
    }

    /// <summary>The draft is stored as the canonical JSON (RFC 8785) of the typed definition, as a Locked version's artefact is, so equal content has equal bytes.</summary>
    private static string CanonicalDraft(ProductArtefact definition) =>
        System.Text.Encoding.UTF8.GetString(CanonicalJson.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(definition, SharedKernelJson.Options)));

    private static DomainError ImportInvalid(string message) =>
        new(ErrorCode.For(ModuleCode.PFC, "IMPORT-INVALID"), message) { FieldErrors = [new FieldError("source", "PFC-IMPORT-INVALID", "pfc.import-invalid", message)] };

    private static DomainError Invalid(string field, string message) =>
        new(ErrorCode.For(ModuleCode.PFC, "IMPORT-INVALID"), message) { FieldErrors = [new FieldError(field, "PFC-IMPORT-INVALID", "pfc.import-invalid", message)] };
}

/// <summary>Audit facts of the import: the new change set; the source text is product data and not logged.</summary>
internal sealed class ImportProductSourceAuditor : ICommandAuditor<ImportProductSource, ProductImportImportResponse>
{
    public CommandAuditFacts Describe(ImportProductSource command, Result<ProductImportImportResponse>? result)
    {
        if (result is not { IsSuccess: true, Value: { Created: true, ChangeSet: { } set } })
        {
            return new CommandAuditFacts();
        }

        var state = set.ChangeSetState;
        return new CommandAuditFacts
        {
            ObjectRef = ChangeSetSupport.Subject(state.ChangeSetId),
            ObjectNumber = $"{state.ProductCode} draft of {state.BaseVersion}",
            BusinessKeys = BusinessKeys.Empty.With("productCode", state.ProductCode).With("changeSetId", state.ChangeSetId.ToString("D")),
            Changes = AuditDiff.Compute(null, new { status = ChangeSetSupport.Open, baseVersion = state.BaseVersion.ToString(), source = "import" }),
        };
    }
}
