using System.Globalization;
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
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Product.Commands;

/// <summary>Shared pieces of the change-set commands (BO-PFC-01): the lint view, the JSON-pointer patch and the save.</summary>
internal static class ChangeSetSupport
{
    public const string Open = "OPEN";
    public const string Abandoned = "ABANDONED";

    public static ObjectRef Subject(Guid changeSetId) => new(ModuleCode.PFC, "ChangeSet", changeSetId.ToString("D"));

    /// <summary>Parses the stored draft; a draft that no longer has the artefact shape cannot be linted and is refused by the caller.</summary>
    public static Result<ProductArtefact> ParseDraft(string json)
    {
        try
        {
            return ArtefactCompiler.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return InvalidEdit(string.Empty, "The edited draft is not a valid product definition: " + ex.Message);
        }
    }

    /// <summary>Lints the draft. Findings never block saving a draft (they block submitting it, BO-PFC-04).</summary>
    public static ChangeSetValidation Validation(ProductArtefact draft)
    {
        var findings = ArtefactCompiler.Lint(draft)
            .Select(f => new ChangeSetFinding { Path = f.Field, Code = f.Code, MessageKey = f.MessageKey, Message = f.Message ?? f.Code })
            .ToList();
        return new ChangeSetValidation { Clean = findings.Count == 0, Findings = findings };
    }

    public static async Task<Result<ChangeSetCreateResponse>> ViewAsync(ProductDbContext db, ChangeSetRow row, string productCode, CancellationToken cancellationToken)
    {
        var draft = ParseDraft(row.DraftJson);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var baseVersion = await db.Versions.AsNoTracking().Where(v => v.ProductVersionId == row.BaseVersionId)
            .Select(v => new { v.Major, v.Minor }).FirstAsync(cancellationToken).ConfigureAwait(false);
        var edits = await db.ChangeSetEdits.AsNoTracking().CountAsync(e => e.ChangeSetId == row.ChangeSetId, cancellationToken).ConfigureAwait(false);
        return new ChangeSetCreateResponse
        {
            ChangeSetState = new ChangeSetState
            {
                ChangeSetId = row.ChangeSetId,
                ProductCode = productCode,
                BaseVersion = new ProductVersionNumber(baseVersion.Major, baseVersion.Minor),
                Status = row.Status switch
                {
                    Open => ChangeSetState.StatusValue.Open,
                    Abandoned => ChangeSetState.StatusValue.Abandoned,
                    _ => ChangeSetState.StatusValue.Submitted,
                },
                RecordVersion = row.RecordVersion,
                EditCount = edits,
                Draft = draft.Value,
            },
            Validation = Validation(draft.Value),
        };
    }

    public static DomainError InvalidEdit(string path, string message) =>
        new(ErrorCode.For(ModuleCode.PFC, "INVALID-EDIT"), message) { FieldErrors = [new FieldError(path, "PFC-INVALID-EDIT", "pfc.invalid-edit", message)] };

    public static async Task<DomainError?> TrySaveAsync(ProductDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.PFC, "STALE", "The change set changed meanwhile; reload and retry.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two edits raced on the same op-log sequence: the loser is stale.
            return DomainError.Of(ModuleCode.PFC, "STALE", "The change set changed meanwhile; reload and retry.");
        }
    }

    /// <summary>Applies one JSON-pointer (RFC 6901) add, replace or remove to the draft; returns the old value's JSON (null when absent).</summary>
    public static Result<string?> Apply(JsonNode root, ChangeSetPatchOperation operation)
    {
        var path = operation.Path;
        if (path.Length < 2 || path[0] != '/')
        {
            return InvalidEdit(path, "The path must be a JSON pointer into the draft, such as /elements/0/parent.");
        }

        var tokens = path[1..].Split('/').Select(t => t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        var node = root;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            node = Child(node, tokens[i]);
            if (node is null)
            {
                return InvalidEdit(path, $"The path does not exist in the draft (at '{tokens[i]}').");
            }
        }

        var last = tokens[^1];
        JsonNode? value = null;
        if (operation.Op != ChangeSetPatchOperation.OpValue.Remove)
        {
            if (operation.Value is not { ValueKind: not JsonValueKind.Undefined } element)
            {
                return InvalidEdit(path, "add and replace need a value.");
            }

            value = JsonNode.Parse(element.GetRawText());
        }

        switch (node)
        {
            case JsonObject obj:
            {
                var exists = obj.ContainsKey(last);
                var old = exists ? obj[last]?.ToJsonString() ?? "null" : null;
                if (operation.Op == ChangeSetPatchOperation.OpValue.Add)
                {
                    obj[last] = value;
                }
                else if (!exists)
                {
                    return InvalidEdit(path, "The path to replace or remove does not exist in the draft.");
                }
                else if (operation.Op == ChangeSetPatchOperation.OpValue.Replace)
                {
                    obj[last] = value;
                }
                else
                {
                    obj.Remove(last);
                }

                return old;
            }

            case JsonArray array:
            {
                if (last == "-" && operation.Op == ChangeSetPatchOperation.OpValue.Add)
                {
                    array.Add(value);
                    return (string?)null;
                }

                if (!int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index > array.Count || (operation.Op != ChangeSetPatchOperation.OpValue.Add && index >= array.Count))
                {
                    return InvalidEdit(path, "The array index is out of range.");
                }

                if (operation.Op == ChangeSetPatchOperation.OpValue.Add)
                {
                    array.Insert(index, value);
                    return (string?)null;
                }

                var old = array[index]?.ToJsonString() ?? "null";
                if (operation.Op == ChangeSetPatchOperation.OpValue.Replace)
                {
                    array[index] = value;
                }
                else
                {
                    array.RemoveAt(index);
                }

                return old;
            }

            default:
                return InvalidEdit(path, "The parent of the path is not an object or array.");
        }
    }

    private static JsonNode? Child(JsonNode node, string token) => node switch
    {
        JsonObject obj => obj.TryGetPropertyValue(token, out var child) ? child : null,
        JsonArray array when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.Count => array[i],
        _ => null,
    };
}

/// <summary><c>pfc.ChangeSet.create</c>: copies a Locked version's artefact into a new draft (REQ-PFC-182). The version itself is never touched.</summary>
internal sealed record CreateChangeSet(ChangeSetCreateRequest Request) : ICommand<ChangeSetCreateResponse>;

internal sealed class CreateChangeSetValidator : AbstractValidator<CreateChangeSet>
{
    public CreateChangeSetValidator() =>
        RuleFor(c => c.Request.ProductCode).NotEmpty().Matches("^[A-Z][A-Z0-9-]{1,39}$").WithErrorCode("PRODUCT_CODE");
}

internal sealed class CreateChangeSetHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities) : ICommandHandler<CreateChangeSet, ChangeSetCreateResponse>
{
    public async Task<Result<ChangeSetCreateResponse>> HandleAsync(CreateChangeSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);

        var product = await db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.Code == request.ProductCode, cancellationToken).ConfigureAwait(false);
        if (product is null)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-PRODUCT", $"Product '{request.ProductCode}' is unknown for this legal entity and jurisdiction.");
        }

        var major = request.BaseVersion.Major;
        var minor = request.BaseVersion.Minor;
        var version = await db.Versions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.ProductId == product.ProductId && v.Major == major && v.Minor == minor && v.Status == "LOCKED", cancellationToken).ConfigureAwait(false);
        var artefact = version is null ? null : await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtefactHash == version.ArtefactHash, cancellationToken).ConfigureAwait(false);
        if (version is null || artefact is null)
        {
            return DomainError.Of(ModuleCode.PFC, "NO-VERSION", $"Version {request.BaseVersion} of '{request.ProductCode}' is not a Locked version.");
        }

        var now = clock.Now;
        var actor = context.Actor.ToString();
        var row = db.ChangeSets.Add(new ChangeSetRow
        {
            ChangeSetId = EntityIds.NewGuid(),
            LegalEntityId = legalEntity,
            Jurisdiction = product.Jurisdiction,
            ProductId = product.ProductId,
            BaseVersionId = version.ProductVersionId,
            Status = ChangeSetSupport.Open,
            DraftJson = artefact.CanonicalJson,
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

        return await ChangeSetSupport.ViewAsync(db, row, product.Code, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CreateChangeSetAuditor : ICommandAuditor<CreateChangeSet, ChangeSetCreateResponse>
{
    public CommandAuditFacts Describe(CreateChangeSet command, Result<ChangeSetCreateResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("productCode", command.Request.ProductCode);
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = new ObjectRef(ModuleCode.PFC, "ProductVersion", $"{command.Request.ProductCode}@{command.Request.BaseVersion}"), BusinessKeys = keys };
        }

        var state = ok.Value.ChangeSetState;
        return new CommandAuditFacts
        {
            ObjectRef = ChangeSetSupport.Subject(state.ChangeSetId),
            ObjectNumber = $"{command.Request.ProductCode} draft of {command.Request.BaseVersion}",
            BusinessKeys = keys.With("changeSetId", state.ChangeSetId.ToString("D")),
            Changes = AuditDiff.Compute(null, new { status = ChangeSetSupport.Open, baseVersion = command.Request.BaseVersion.ToString() }),
        };
    }
}

/// <summary><c>pfc.ChangeSet.edit</c>: applies JSON-pointer operations to the draft, logs each one, lints and saves even with findings (REQ-PFC-183).</summary>
internal sealed record EditChangeSet(ChangeSetEditRequest Request) : ICommand<ChangeSetEditResponse>;

internal sealed class EditChangeSetValidator : AbstractValidator<EditChangeSet>
{
    public EditChangeSetValidator()
    {
        RuleFor(c => c.Request.ChangeSetId).NotEmpty();
        RuleFor(c => c.Request.RecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Edits).NotEmpty().Must(e => e.Count <= 100).WithErrorCode("EDITS");
        RuleForEach(c => c.Request.Edits).ChildRules(e => e.RuleFor(x => x.Path).NotEmpty().MaximumLength(500));
    }
}

internal sealed class EditChangeSetHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities) : ICommandHandler<EditChangeSet, ChangeSetEditResponse>
{
    public async Task<Result<ChangeSetEditResponse>> HandleAsync(EditChangeSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);

        var row = await db.ChangeSets
            .FromSql($"SELECT * FROM pfc.change_set WHERE change_set_id = {request.ChangeSetId} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PFC, "CHANGE-SET-NOT-FOUND", "The change set does not exist.");
        }

        if (row.Status != ChangeSetSupport.Open)
        {
            return DomainError.Of(ModuleCode.PFC, "CHANGE-SET-STATE", $"The change set is {row.Status}; only an open change set can be edited.");
        }

        if (row.RecordVersion != request.RecordVersion)
        {
            return DomainError.Of(ModuleCode.PFC, "STALE", "The change set changed meanwhile; reload and retry.");
        }

        var root = JsonNode.Parse(row.DraftJson) ?? throw new InvalidOperationException("The stored draft is empty.");
        var now = clock.Now;
        var actor = context.Actor.ToString();
        var seq = await db.ChangeSetEdits.AsNoTracking().Where(e => e.ChangeSetId == row.ChangeSetId)
            .MaxAsync(e => (int?)e.Seq, cancellationToken).ConfigureAwait(false) ?? 0;
        var log = new List<ChangeSetEditRow>();
        foreach (var operation in request.Edits)
        {
            var old = ChangeSetSupport.Apply(root, operation);
            if (old.IsFailure)
            {
                return old.Error;
            }

            log.Add(new ChangeSetEditRow
            {
                EditId = EntityIds.NewGuid(),
                ChangeSetId = row.ChangeSetId,
                Seq = ++seq,
                Op = operation.Op.ToString().ToLowerInvariant(),
                Path = operation.Path,
                OldJson = old.Value,
                NewJson = operation.Op == ChangeSetPatchOperation.OpValue.Remove ? null : operation.Value!.Value.GetRawText(),
                Actor = actor,
                At = now,
            });
        }

        var edited = root.ToJsonString();
        var parsed = ChangeSetSupport.ParseDraft(edited);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        row.DraftJson = edited;
        row.RecordVersion++;
        row.UpdatedAt = now;
        row.UpdatedBy = actor;
        db.ChangeSetEdits.AddRange(log);
        if (await ChangeSetSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        var productCode = await db.Products.AsNoTracking().Where(p => p.ProductId == row.ProductId).Select(p => p.Code).FirstAsync(cancellationToken).ConfigureAwait(false);
        var view = await ChangeSetSupport.ViewAsync(db, row, productCode, cancellationToken).ConfigureAwait(false);
        return view.IsFailure
            ? view.Error
            : new ChangeSetEditResponse { ChangeSetState = view.Value.ChangeSetState, Validation = view.Value.Validation };
    }
}

internal sealed class EditChangeSetAuditor : ICommandAuditor<EditChangeSet, ChangeSetEditResponse>
{
    public CommandAuditFacts Describe(EditChangeSet command, Result<ChangeSetEditResponse>? result) => new()
    {
        ObjectRef = ChangeSetSupport.Subject(command.Request.ChangeSetId),
        BusinessKeys = BusinessKeys.Empty.With("changeSetId", command.Request.ChangeSetId.ToString("D")),
        Changes = result is { IsSuccess: true } ok
            ? AuditDiff.Compute(null, new { edits = command.Request.Edits.Select(e => $"{e.Op} {e.Path}").ToArray(), recordVersion = ok.Value.ChangeSetState.RecordVersion, clean = ok.Value.Validation.Clean })
            : [],
    };
}

/// <summary><c>pfc.ChangeSet.abandon</c>: an open change set becomes ABANDONED; the draft and its log are kept.</summary>
internal sealed record AbandonChangeSet(ChangeSetAbandonRequest Request) : ICommand<ChangeSetAbandonResponse>;

internal sealed class AbandonChangeSetValidator : AbstractValidator<AbandonChangeSet>
{
    public AbandonChangeSetValidator()
    {
        RuleFor(c => c.Request.ChangeSetId).NotEmpty();
        RuleFor(c => c.Request.RecordVersion).GreaterThanOrEqualTo(1);
    }
}

internal sealed class AbandonChangeSetHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities) : ICommandHandler<AbandonChangeSet, ChangeSetAbandonResponse>
{
    public async Task<Result<ChangeSetAbandonResponse>> HandleAsync(AbandonChangeSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);

        var row = await db.ChangeSets
            .FromSql($"SELECT * FROM pfc.change_set WHERE change_set_id = {request.ChangeSetId} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PFC, "CHANGE-SET-NOT-FOUND", "The change set does not exist.");
        }

        if (row.Status != ChangeSetSupport.Open)
        {
            return DomainError.Of(ModuleCode.PFC, "CHANGE-SET-STATE", $"The change set is {row.Status}; only an open change set can be abandoned.");
        }

        if (row.RecordVersion != request.RecordVersion)
        {
            return DomainError.Of(ModuleCode.PFC, "STALE", "The change set changed meanwhile; reload and retry.");
        }

        row.Status = ChangeSetSupport.Abandoned;
        row.RecordVersion++;
        row.UpdatedAt = clock.Now;
        row.UpdatedBy = context.Actor.ToString();
        if (await ChangeSetSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        var productCode = await db.Products.AsNoTracking().Where(p => p.ProductId == row.ProductId).Select(p => p.Code).FirstAsync(cancellationToken).ConfigureAwait(false);
        var view = await ChangeSetSupport.ViewAsync(db, row, productCode, cancellationToken).ConfigureAwait(false);
        return view.IsFailure
            ? view.Error
            : new ChangeSetAbandonResponse { ChangeSetState = view.Value.ChangeSetState, Validation = view.Value.Validation };
    }
}

internal sealed class AbandonChangeSetAuditor : ICommandAuditor<AbandonChangeSet, ChangeSetAbandonResponse>
{
    public CommandAuditFacts Describe(AbandonChangeSet command, Result<ChangeSetAbandonResponse>? result) => new()
    {
        ObjectRef = ChangeSetSupport.Subject(command.Request.ChangeSetId),
        BusinessKeys = BusinessKeys.Empty.With("changeSetId", command.Request.ChangeSetId.ToString("D")),
        Changes = result is { IsSuccess: true } ? AuditDiff.Compute(new { status = ChangeSetSupport.Open }, new { status = ChangeSetSupport.Abandoned }) : [],
    };
}
