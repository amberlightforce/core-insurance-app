using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Contracts.Events;
using CoreIns.Modules.Product.Domain;
using CoreIns.Modules.Product.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Product.Commands;

/// <summary>
/// <c>pfc.ProductVersion.import</c>: loads a product source document as data (REQ-PFC-031, -032, -162). The seed and the
/// later authoring workflow use this one path: lint and compile to the canonical artefact, store the product, the version
/// and the artefact, and, with <c>lock</c>, take the version Draft, Submitted, Approved, Locked in one step.
/// </summary>
internal sealed record ImportProductVersion(ProductVersionImportRequest Request) : ICommand<ProductVersionImportResponse>;

/// <summary>Shape rules; the content rules are the compiler's lint (PFC-LINT-*).</summary>
internal sealed class ImportProductVersionValidator : AbstractValidator<ImportProductVersion>
{
    public ImportProductVersionValidator()
    {
        RuleFor(c => c.Request.Definition.Product.Code).NotEmpty().Matches("^[A-Z][A-Z0-9-]{1,39}$").WithErrorCode("PRODUCT_CODE");
        RuleFor(c => c.Request.Definition.ProductLine.Code).NotEmpty().MaximumLength(40);
        RuleFor(c => c.Request.Definition.LegalEntity).NotEmpty().MaximumLength(40);
        RuleFor(c => c.Request.Definition.Jurisdiction).Matches("^[A-Z]{2}$").WithErrorCode("JURISDICTION");
    }
}

internal sealed class ImportProductVersionHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IEventPublisher events) : ICommandHandler<ImportProductVersion, ProductVersionImportResponse>
{
    public async Task<Result<ProductVersionImportResponse>> HandleAsync(ImportProductVersion command, CancellationToken cancellationToken)
    {
        var definition = command.Request.Definition;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        if (!string.Equals(definition.LegalEntity, caller.Value, StringComparison.Ordinal))
        {
            return Invalid("legalEntity", "PFC-LINT-LEGAL-ENTITY", "The definition belongs to another legal entity than the caller's.");
        }

        var compiled = ArtefactCompiler.Compile(definition);
        if (compiled.IsFailure)
        {
            return compiled.Error;
        }

        var legalEntity = legalEntities.Resolve(caller);
        var now = clock.Now;
        var actor = context.Actor.ToString();
        var artefact = compiled.Value;
        var number = definition.Version;

        // Product: unique per legal entity; immutable after its first Locked version (REQ-PFC-031).
        var product = await db.Products.FirstOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.Code == definition.Product.Code, cancellationToken).ConfigureAwait(false);
        if (product is not null && product.Jurisdiction != definition.Jurisdiction)
        {
            return Invalid("jurisdiction", "PFC-LINT-PRODUCT-JURISDICTION", "The product exists in another jurisdiction.");
        }

        var existing = product is null
            ? null
            : await db.Versions.FirstOrDefaultAsync(v => v.ProductId == product.ProductId && v.Major == number.Major && v.Minor == number.Minor, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.ArtefactHash, artefact.Hash.Value, StringComparison.Ordinal))
            {
                return DomainError.Of(ModuleCode.PFC, "VERSION-EXISTS", $"Version {number} of '{definition.Product.Code}' exists with a different artefact; publish a new version instead.");
            }

            if (existing.Status == Codes.Of(ProductVersionState.Draft) && command.Request.Lock)
            {
                return await LockAsync(existing, product!, definition, now, cancellationToken).ConfigureAwait(false);
            }

            return Respond(existing, definition, created: false);
        }

        product ??= db.Products.Add(new ProductRow
        {
            ProductId = ProductId.New(),
            LegalEntityId = legalEntity,
            Jurisdiction = definition.Jurisdiction,
            Code = definition.Product.Code,
            LineCode = definition.ProductLine.Code,
            LineFamily = definition.ProductLine.Family,
            NameEl = definition.Product.Name.El,
            NameEn = definition.Product.Name.En,
            ProductType = definition.Product.ProductType,
            CustomerType = definition.Product.CustomerType,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
        }).Entity;

        if (await db.Artifacts.FindAsync([artefact.Hash.Value], cancellationToken).ConfigureAwait(false) is null)
        {
            db.Artifacts.Add(new ArtifactRow { ArtefactHash = artefact.Hash.Value, CanonicalJson = artefact.CanonicalJson, SizeBytes = artefact.SizeBytes, StoredAt = now });
        }

        var version = db.Versions.Add(new ProductVersionRow
        {
            ProductVersionId = ProductVersionId.New(),
            ProductId = product.ProductId,
            LegalEntityId = legalEntity,
            Jurisdiction = definition.Jurisdiction,
            Major = number.Major,
            Minor = number.Minor,
            Status = Codes.Of(ProductVersionStateModel.Machine.Start(ProductVersionState.Draft).Value),
            IsAbstract = definition.IsAbstract,
            Channels = [.. definition.Channels],
            ContractCurrency = definition.ContractCurrency.Code,
            NewBusinessFrom = definition.Windows.NewBusiness.Start,
            NewBusinessTo = definition.Windows.NewBusiness.End,
            RenewalFrom = definition.Windows.Renewal.Start,
            RenewalTo = definition.Windows.Renewal.End,
            ArtefactHash = artefact.Hash.Value,
            SchemaVersion = definition.SchemaVersion,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
        }).Entity;

        return command.Request.Lock
            ? await LockAsync(version, product, definition, now, cancellationToken).ConfigureAwait(false)
            : await SaveAsync(version, definition, created: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Draft, Submitted, Approved, Locked through the state model (REQ-PFC-162); the approval workflow replaces this later.</summary>
    private async Task<Result<ProductVersionImportResponse>> LockAsync(
        ProductVersionRow version, ProductRow product, ProductArtefact definition, Instant now, CancellationToken cancellationToken)
    {
        var state = Codes.Parse<ProductVersionState>(version.Status);
        foreach (var trigger in new[] { ProductVersionTrigger.Submit, ProductVersionTrigger.Approve, ProductVersionTrigger.Publish })
        {
            var next = ProductVersionStateModel.Machine.Fire(state, trigger);
            if (next.IsFailure)
            {
                return next.Error;
            }

            state = next.Value;
        }

        // Publishing a successor closes the predecessor's open new-business window in the same transaction (REQ-PFC-033);
        // any other intersection with a Locked version is refused (BR-PFC-001, also enforced by an exclusion constraint).
        var window = definition.Windows.NewBusiness;
        var locked = await db.Versions
            .Where(v => v.ProductId == product.ProductId && v.Status == "LOCKED" && v.ProductVersionId != version.ProductVersionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var other in locked.Where(o => new DateRange(o.NewBusinessFrom, o.NewBusinessTo).Overlaps(window)))
        {
            var older = new ProductVersionNumber(other.Major, other.Minor) < definition.Version;
            if (older && other.NewBusinessTo is null && other.NewBusinessFrom < window.Start)
            {
                other.NewBusinessTo = window.Start;
                other.RecordVersion++;
                continue;
            }

            return DomainError.Of(ModuleCode.PFC, "WINDOW-OVERLAP", $"Locked version {other.Major}.{other.Minor} already covers part of the new-business window.");
        }

        if (db.ChangeTracker.Entries<ProductVersionRow>().Any(e => e.State == EntityState.Modified))
        {
            // Close the predecessor's window before the successor becomes Locked, so the exclusion constraint never sees both open.
            var closed = await TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (closed is not null)
            {
                return closed;
            }
        }

        version.Status = Codes.Of(state);
        version.LifecycleSubstate = Codes.Of(ProductVersionLockedSubstate.Active);
        version.LockedAt = now;

        var saved = await SaveAsync(version, definition, created: true, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ProductVersionPublishedV1.Descriptor),
            "Product",
            product.ProductId.Value.ToString(),
            new ProductVersionPublishedV1
            {
                ProductCode = definition.Product.Code,
                Version = definition.Version,
                ArtefactHash = Sha256Hash.Parse(version.ArtefactHash),
                NewBusinessWindow = ToInstants(definition.Windows.NewBusiness),
                RenewalWindow = ToInstants(definition.Windows.Renewal),
                Jurisdiction = definition.Jurisdiction,
                LegalEntity = definition.LegalEntity,
                Channels = definition.Channels,
                IpidChanged = false,
                ChangedAreas = [],
                RatingSlotDeclaration = definition.References.Rating,
            },
            BusinessKeys.Empty.With("productId", product.ProductId.Value.ToString())));
        return saved;
    }

    private async Task<DomainError?> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation })
        {
            return DomainError.Of(ModuleCode.PFC, "WINDOW-OVERLAP", "Another Locked version covers part of the new-business window.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return DomainError.Of(ModuleCode.PFC, "VERSION-EXISTS", "The product or version was created meanwhile; import again.");
        }
    }

    private async Task<Result<ProductVersionImportResponse>> SaveAsync(ProductVersionRow version, ProductArtefact definition, bool created, CancellationToken cancellationToken)
    {
        // Save inside the handler so a lost race on a unique or exclusion constraint becomes a typed error.
        if (await TrySaveAsync(cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        return Respond(version, definition, created);
    }

    private static ProductVersionImportResponse Respond(ProductVersionRow version, ProductArtefact definition, bool created) => new()
    {
        Product = definition.Product.Code,
        Version = new ProductVersionNumber(version.Major, version.Minor),
        ArtefactHash = Sha256Hash.Parse(version.ArtefactHash),
        Status = version.Status == "LOCKED" ? ProductVersionImportResponse.StatusValue.Locked : ProductVersionImportResponse.StatusValue.Draft,
        Created = created,
    };

    private static InstantRange ToInstants(DateRange window)
    {
        var zone = AthensOrUtc();
        return new InstantRange(window.Start.StartOfDayIn(zone), window.End?.StartOfDayIn(zone));
    }

    private static TimeZoneInfo AthensOrUtc()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DomainError Invalid(string field, string code, string message) =>
        new(ErrorCode.For(ModuleCode.PFC, "INVALID-DEFINITION"), message) { FieldErrors = [new FieldError(field, code, "pfc.lint." + code.ToLowerInvariant(), message)] };
}

/// <summary>Audit facts of the import: object and version; the definition is product data (P0), not logged in full.</summary>
internal sealed class ImportProductVersionAuditor : ICommandAuditor<ImportProductVersion, ProductVersionImportResponse>
{
    public CommandAuditFacts Describe(ImportProductVersion command, Result<ProductVersionImportResponse>? result)
    {
        if (result is not { IsSuccess: true } success)
        {
            return new CommandAuditFacts();
        }

        var response = success.Value;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.PFC, "ProductVersion", $"{response.Product}@{response.Version}"),
            ObjectNumber = $"{response.Product} {response.Version}",
            BusinessKeys = BusinessKeys.Empty.With("productCode", response.Product),
            Changes = AuditDiff.Compute(null, new { product = response.Product, version = response.Version.ToString(), artefactHash = response.ArtefactHash.Value, status = response.Status.ToString(), response.Created }),
        };
    }
}
