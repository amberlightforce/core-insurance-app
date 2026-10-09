using CoreIns.Modules.Product.Authority;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Contracts.Events;
using CoreIns.Modules.Product.Domain;
using CoreIns.Modules.Product.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CoreIns.Modules.Product.Commands;

/// <summary>
/// Shared pieces of the fall-back commands (REQ-PFC-213, D-SL5-09): the bound content hash, the subject, the facts loader and
/// the view. The content is derived by the server from stored rows only; the client names the product, the defective version
/// and a reason, nothing else (PITFALLS 4, 7).
/// </summary>
internal static class FallbackSupport
{
    public const string Pending = "PENDING_APPROVAL";
    public const string Applied = "APPLIED";
    public const string Rejected = "REJECTED";

    public static ObjectRef Subject(Guid fallbackId) => new(ModuleCode.PFC, ProductAuthorityTypes.FallbackSubjectType, fallbackId.ToString("D"));

    public static string ActorKey(ActorRef actor) => $"{actor.KindCode}:{actor.Id}";

    /// <summary>The frozen configuration PLT approves. Effective windows are derived from the checker's decision instant.</summary>
    public static Sha256Hash ContentHash(
        string legalEntity, string jurisdiction, string productCode, ProductVersionNumber defective, string defectiveArtefactHash,
        ProductVersionNumber source, string sourceArtefactHash, ProductVersionNumber newVersion, string reason) =>
        CanonicalJson.HashOf(
            new
            {
                type = ProductAuthorityTypes.FallbackApprovalType,
                legalEntity,
                jurisdiction,
                product = productCode,
                defective = defective.ToString(),
                defectiveArtefactHash,
                source = source.ToString(),
                sourceArtefactHash,
                newVersion = newVersion.ToString(),
                reason,
            },
            SharedKernelJson.Options);

    public static async Task<List<VersionFacts>> LoadFactsAsync(ProductDbContext db, ProductId productId, CancellationToken cancellationToken)
    {
        var rows = await db.Versions.AsNoTracking().Where(v => v.ProductId == productId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var replaced = rows.Where(r => r.ReplacesVersionId is not null).Select(r => r.ReplacesVersionId!.Value).ToHashSet();
        return
        [
            .. rows.Select(r => new VersionFacts(
                r.ProductVersionId,
                new ProductVersionNumber(r.Major, r.Minor),
                r.Status,
                r.IsAbstract,
                r.Channels,
                new DateRange(r.NewBusinessFrom, r.NewBusinessTo),
                new DateRange(r.RenewalFrom, r.RenewalTo),
                r.ArtefactHash,
                replaced.Contains(r.ProductVersionId))),
        ];
    }

    public static InstantRange ToInstants(DateRange window) =>
        new(window.Start.StartOfDayIn(FallbackPlanner.Athens), window.End?.StartOfDayIn(FallbackPlanner.Athens));

    public static FallbackView View(FallbackRequestRow row, string productCode) => new()
    {
        FallbackId = row.FallbackId,
        ProductCode = productCode,
        DefectiveVersion = default,
        NewVersion = row.Status == Applied ? new ProductVersionNumber(row.NewMajor, row.NewMinor) : null,
        Status = row.Status switch
        {
            Applied => FallbackStatus.Applied,
            Rejected => FallbackStatus.Rejected,
            _ => FallbackStatus.PendingApproval,
        },
        Reason = row.Reason,
        RequestedBy = row.RequestedBy,
        DecidedBy = row.DecidedBy,
        ApprovalRequestId = new ApprovalRequestId(row.ApprovalRequestId),
        DecidedAt = row.DecidedAt,
    };

    /// <summary>A unique or exclusion violation on save is a lost race (409), never a 500 (PITFALLS 15).</summary>
    public static async Task<DomainError?> TrySaveAsync(ProductDbContext db, CancellationToken cancellationToken)
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
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-STATE", "A fall-back or version for this product was created meanwhile; reload and retry.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.PFC, "STALE", "The product changed meanwhile; reload and retry.");
        }
    }
}

/// <summary><c>pfc.ProductVersion.fallback</c>: the maker's request (dry run previews, a real call creates the pending fall-back and its approval).</summary>
internal sealed record RequestFallback(ProductVersionEmergencyFallbackRequest Request) : ICommand<ProductVersionEmergencyFallbackResponse>;

internal sealed class RequestFallbackValidator : AbstractValidator<RequestFallback>
{
    public RequestFallbackValidator()
    {
        RuleFor(c => c.Request.ProductCode).NotEmpty().Matches("^[A-Z][A-Z0-9-]{1,39}$").WithErrorCode("PRODUCT_CODE");
        RuleFor(c => c.Request.Reason).NotEmpty().MinimumLength(20).MaximumLength(128).WithErrorCode("REASON");
        RuleFor(c => c.Request.Reason).Must(r => r is null || r.Trim().Length >= 20).WithErrorCode("REASON");
    }
}

internal sealed class RequestFallbackHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IPlatformApprovalService approvals) : ICommandHandler<RequestFallback, ProductVersionEmergencyFallbackResponse>
{
    public async Task<Result<ProductVersionEmergencyFallbackResponse>> HandleAsync(RequestFallback command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);

        // The product row is the lock that serialises fall-backs and publications of one product.
        var product = await db.Products
            .FromSql($"SELECT * FROM pfc.product WHERE legal_entity_id = {legalEntity.Value} AND code = {request.ProductCode} FOR UPDATE")
            .AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (product is null)
        {
            return DomainError.Of(ModuleCode.PFC, "UNKNOWN-PRODUCT", $"Product '{request.ProductCode}' is unknown for this legal entity and jurisdiction.");
        }

        var now = clock.Now;
        var fallbackDate = FallbackPlanner.AthensDate(now);
        var facts = await FallbackSupport.LoadFactsAsync(db, product.ProductId, cancellationToken).ConfigureAwait(false);
        var planned = FallbackPlanner.Plan(facts, request.DefectiveVersion, fallbackDate);
        if (planned.IsFailure)
        {
            return planned.Error;
        }

        var plan = planned.Value;
        var live = await db.Fallbacks.AsNoTracking()
            .AnyAsync(f => f.DefectiveVersionId == plan.Defective.Id && f.Status != FallbackSupport.Rejected, cancellationToken).ConfigureAwait(false);
        if (live)
        {
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-STATE", $"A fall-back for version {plan.Defective.Number} is already pending or applied.");
        }

        var preview = new FallbackPreview
        {
            NewVersion = plan.NewVersion,
            Source = new FallbackSource { Version = plan.Source.Number, ArtefactHash = Sha256Hash.Parse(plan.Source.ArtefactHash) },
            NewBusinessWindow = FallbackSupport.ToInstants(plan.NewBusinessWindow),
            ClosedWindow = FallbackSupport.ToInstants(plan.ClosedWindow),
        };
        if (context.DryRun)
        {
            return new ProductVersionEmergencyFallbackResponse { DryRun = true, Preview = preview, FallbackId = null, Status = null, ApprovalRequestId = null };
        }

        var fallbackId = EntityIds.NewGuid();
        var hash = FallbackSupport.ContentHash(
            caller.Value, product.Jurisdiction, product.Code, plan.Defective.Number, plan.Defective.ArtefactHash,
            plan.Source.Number, plan.Source.ArtefactHash, plan.NewVersion, request.Reason);
        ApprovalRequestResponse approval;
        try
        {
            approval = await approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = ProductAuthorityTypes.FallbackApprovalType,
                    ObjectRef = FallbackSupport.Subject(fallbackId),
                    PayloadHash = hash,
                    Authority = new ApprovalAuthority
                    {
                        Type = ProductAuthorityTypes.EmergencyChange.Value,
                        Codes = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [ProductAuthorityTypes.ProductLineDimension] = product.LineCode,
                            [ProductAuthorityTypes.JurisdictionDimension] = product.Jurisdiction,
                        },
                    },
                    ReferralRole = ProductAuthorityTypes.CheckerRole,
                    Reason = $"Fall-back of {product.Code} {plan.Defective.Number} to a copy of {plan.Source.Number} ({plan.NewVersion}), effective on the approval's Athens business date.",
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        db.Fallbacks.Add(new FallbackRequestRow
        {
            FallbackId = fallbackId,
            LegalEntityId = legalEntity,
            Jurisdiction = product.Jurisdiction,
            ProductId = product.ProductId,
            DefectiveVersionId = plan.Defective.Id,
            SourceVersionId = plan.Source.Id,
            NewMajor = plan.NewVersion.Major,
            NewMinor = plan.NewVersion.Minor,
            FallbackDate = fallbackDate,
            Status = FallbackSupport.Pending,
            Reason = request.Reason,
            PayloadHash = hash.Value,
            ApprovalRequestId = approval.Request.RequestId,
            RequestedBy = FallbackSupport.ActorKey(context.Actor),
            RequestedByPrincipal = context.OnBehalfOf is { } principal ? FallbackSupport.ActorKey(principal) : null,
            RequestedAt = now,
            RecordVersion = 1,
        });
        if (await FallbackSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        return new ProductVersionEmergencyFallbackResponse
        {
            DryRun = false,
            Preview = preview,
            FallbackId = fallbackId,
            Status = FallbackStatus.PendingApproval,
            ApprovalRequestId = new ApprovalRequestId(approval.Request.RequestId),
        };
    }
}

internal sealed class RequestFallbackAuditor : ICommandAuditor<RequestFallback, ProductVersionEmergencyFallbackResponse>
{
    public CommandAuditFacts Describe(RequestFallback command, Result<ProductVersionEmergencyFallbackResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("productCode", command.Request.ProductCode);
        if (result is not { IsSuccess: true, Value.FallbackId: { } id } ok)
        {
            return new CommandAuditFacts { ObjectRef = new ObjectRef(ModuleCode.PFC, "ProductVersion", $"{command.Request.ProductCode}@{command.Request.DefectiveVersion}"), BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = FallbackSupport.Subject(id),
            ObjectNumber = $"{command.Request.ProductCode} {command.Request.DefectiveVersion}",
            BusinessKeys = keys.With("fallbackId", id.ToString("D")),
            Changes = AuditDiff.Compute(null, new
            {
                status = FallbackSupport.Pending,
                defective = command.Request.DefectiveVersion.ToString(),
                newVersion = ok.Value.Preview?.NewVersion.ToString(),
                source = ok.Value.Preview?.Source.Version.ToString(),
                approvalRequestId = ok.Value.ApprovalRequestId?.Value.ToString("D"),
            }),
        };
    }
}

/// <summary><c>pfc.ProductVersion.decideFallback</c>: the checker's decision; on APPROVE the fall-back is executed in this transaction.</summary>
internal sealed record DecideFallback(ProductVersionDecideFallbackRequest Request) : ICommand<ProductVersionDecideFallbackResponse>;

internal sealed class DecideFallbackValidator : AbstractValidator<DecideFallback>
{
    public DecideFallbackValidator()
    {
        RuleFor(c => c.Request.FallbackId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(128).WithErrorCode("REASON");
    }
}

/// <summary>
/// The checker's path. Order: load and lock the request; verify the stored content still hashes to the bound hash; the checker is
/// neither the maker nor the maker's principal; PLT decides (human checker, authority <c>PFC.EMERGENCY_CHANGE</c>, SoD, hash);
/// on APPROVE <c>verifyForExecution</c> binds subject + type + hash and the decided authority is compared with the dimensions PFC
/// computes itself; the plan is re-derived at the decision instant (it must equal the bound one); then one transaction writes
/// the new write-once version, shortens the defective version's new-business window and publishes the event.
/// A request already decided in the generic PLT inbox converges here: the inbox decision is honoured and executed by the next
/// caller, never lost (PITFALLS 48).
/// </summary>
internal sealed partial class DecideFallbackHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IPlatformApprovalService approvals,
    IEventPublisher events,
    ILogger<DecideFallbackHandler> logger) : ICommandHandler<DecideFallback, ProductVersionDecideFallbackResponse>
{
    public async Task<Result<ProductVersionDecideFallbackResponse>> HandleAsync(DecideFallback command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);

        var row = await db.Fallbacks
            .FromSql($"SELECT * FROM pfc.fallback_request WHERE fallback_id = {request.FallbackId} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-NOT-FOUND", "The fall-back request does not exist.");
        }

        if (row.Status != FallbackSupport.Pending)
        {
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-STATE", $"The fall-back is {row.Status}, not {FallbackSupport.Pending}.");
        }

        var product = await db.Products
            .FromSql($"SELECT * FROM pfc.product WHERE product_id = {row.ProductId.Value} FOR UPDATE")
            .AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var versions = await db.Versions.Where(v => v.ProductVersionId == row.DefectiveVersionId || v.ProductVersionId == row.SourceVersionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var defective = versions.Single(v => v.ProductVersionId == row.DefectiveVersionId);
        var source = versions.Single(v => v.ProductVersionId == row.SourceVersionId);
        var newNumber = new ProductVersionNumber(row.NewMajor, row.NewMinor);
        var defectiveNumber = new ProductVersionNumber(defective.Major, defective.Minor);
        var sourceNumber = new ProductVersionNumber(source.Major, source.Minor);

        // 1. The stored content must still be the content the approval was requested for (a changed row is a tampered request).
        var hash = FallbackSupport.ContentHash(
            caller.Value, product.Jurisdiction, product.Code, defectiveNumber, defective.ArtefactHash, sourceNumber, source.ArtefactHash,
            newNumber, row.Reason);
        if (hash.Value != row.PayloadHash)
        {
            LogRefused(logger, row.FallbackId, "stored content no longer hashes to the bound hash");
            return Sod("The fall-back request no longer matches the content that was submitted for approval.");
        }

        // 2. Four eyes across delegation: neither the caller nor the person the caller acts for is the maker or the maker's principal.
        var callerKeys = context.OnBehalfOf is { } principal
            ? new[] { FallbackSupport.ActorKey(context.Actor), FallbackSupport.ActorKey(principal) }
            : [FallbackSupport.ActorKey(context.Actor)];
        var makerKeys = row.RequestedByPrincipal is null ? new[] { row.RequestedBy } : [row.RequestedBy, row.RequestedByPrincipal];
        if (callerKeys.Any(k => makerKeys.Contains(k, StringComparer.Ordinal)))
        {
            return DomainError.Of(ModuleCode.PLT, "SELF-APPROVAL", "The maker cannot decide their own fall-back request.");
        }

        if (context.Actor.Kind != ActorKind.User)
        {
            return DomainError.Of(ModuleCode.PLT, "CHECKER-MUST-BE-HUMAN", "Only a person can decide a fall-back request.");
        }

        var approve = request.Decision == ProductVersionDecideFallbackRequest.DecisionValue.Approve;
        var subject = FallbackSupport.Subject(row.FallbackId);
        var now = clock.Now;

        // 3. The PLT request must be this fall-back's (type, subject, hash).
        ApprovalGetResponse plt;
        try
        {
            plt = await approvals.GetAsync(row.ApprovalRequestId.ToString("D"), cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        if (plt.Request.Type != ProductAuthorityTypes.FallbackApprovalType || plt.Request.ObjectRef != subject || plt.Request.PayloadHash != hash)
        {
            LogRefused(logger, row.FallbackId, "the PLT request is not bound to this fall-back");
            return Sod("The approval request is not bound to this fall-back.");
        }

        string decidedBy;
        Instant decidedAt = now;
        string decisionReason = request.Reason;
        switch (plt.Request.Status)
        {
            case ApprovalStatus.PendingApproval:
                if (approve)
                {
                    // Validate before PLT records anything, so a stale plan never leaves an approval behind (the pipeline rolls back on failure anyway).
                    var early = await ReplanAsync(row, product.ProductId, now, cancellationToken).ConfigureAwait(false);
                    if (early.IsFailure)
                    {
                        return early.Error;
                    }
                }

                try
                {
                    var decided = await approvals.DecideAsync(
                        new ApprovalDecideRequest
                        {
                            RequestId = row.ApprovalRequestId,
                            Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject,
                            PayloadHash = hash,
                            Comment = request.Reason,
                        },
                        CommandOptions.New(),
                        cancellationToken).ConfigureAwait(false);
                    decidedBy = FallbackSupport.ActorKey(context.Actor);
                    decidedAt = decided.Decision.DecidedAt;
                }
                catch (DomainException ex)
                {
                    return ex.Error;
                }

                break;

            case ApprovalStatus.Approved when plt.Decision is { } d && d.Decision == ApprovalDecisionView.DecisionValue.Approved:
                if (!approve)
                {
                    return DomainError.Of(ModuleCode.PFC, "FALLBACK-STATE", "The approval request was already approved; execute it with APPROVE.");
                }

                decidedBy = $"{d.Checker.Kind.ToString().ToUpperInvariant()}:{d.Checker.Id}";
                decidedAt = d.DecidedAt;
                decisionReason = d.Comment ?? request.Reason;
                break;

            default:
                // Rejected, withdrawn or expired in PLT: the fall-back ends rejected; an APPROVE call cannot resurrect it.
                if (approve)
                {
                    return DomainError.Of(ModuleCode.PFC, "FALLBACK-STATE", $"The approval request is {plt.Request.Status}; the fall-back cannot be executed.");
                }

                decidedBy = plt.Decision is { } r ? $"{r.Checker.Kind.ToString().ToUpperInvariant()}:{r.Checker.Id}" : FallbackSupport.ActorKey(context.Actor);
                decidedAt = plt.Decision?.DecidedAt ?? now;
                decisionReason = plt.Decision?.Comment ?? request.Reason;
                break;
        }

        if (!approve)
        {
            return await FinishAsync(row, product, FallbackSupport.Rejected, decidedBy, decidedAt, decisionReason, null, cancellationToken).ConfigureAwait(false);
        }

        // 4. Execution: the approval must verify for exactly this subject, type and hash, with the authority PFC computes itself.
        ApprovalVerifyForExecutionResponse verified;
        try
        {
            verified = await approvals.VerifyForExecutionAsync(
                new ApprovalVerifyForExecutionRequest
                {
                    RequestId = row.ApprovalRequestId,
                    Hash = hash,
                    Type = ProductAuthorityTypes.FallbackApprovalType,
                    ObjectRef = subject,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            LogRefused(logger, row.FallbackId, ex.Error.Code.Value);
            return Sod($"PLT refused execution ({ex.Error.Code}).");
        }

        if (!verified.Ok
            || verified.Authority.Type != ProductAuthorityTypes.EmergencyChange.Value
            || verified.Authority.Codes?.GetValueOrDefault(ProductAuthorityTypes.ProductLineDimension) != product.LineCode
            || verified.Authority.Codes?.GetValueOrDefault(ProductAuthorityTypes.JurisdictionDimension) != product.Jurisdiction)
        {
            LogRefused(logger, row.FallbackId, "the decided authority does not match");
            return Sod("The approval is not decided under the emergency-change authority for this product line and jurisdiction.");
        }

        // 5. The source and number remain frozen; the business date is the actual approval instant in Athens.
        var replanned = await ReplanAsync(row, product.ProductId, decidedAt, cancellationToken).ConfigureAwait(false);
        if (replanned.IsFailure)
        {
            return replanned.Error;
        }

        return await ApplyAsync(row, product, defective, source, replanned.Value, decidedBy, decidedAt, decisionReason, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<FallbackPlan>> ReplanAsync(FallbackRequestRow row, ProductId productId, Instant now, CancellationToken cancellationToken)
    {
        var today = FallbackPlanner.AthensDate(now);
        var facts = await FallbackSupport.LoadFactsAsync(db, productId, cancellationToken).ConfigureAwait(false);
        var defectiveFacts = facts.FirstOrDefault(f => f.Id == row.DefectiveVersionId);
        if (defectiveFacts is null)
        {
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-SOURCE", "The defective version no longer exists.");
        }

        var planned = FallbackPlanner.Plan(facts, defectiveFacts.Number, today);
        if (planned.IsFailure)
        {
            return planned.Error;
        }

        var plan = planned.Value;
        if (plan.Source.Id != row.SourceVersionId || plan.NewVersion != new ProductVersionNumber(row.NewMajor, row.NewMinor))
        {
            return DomainError.Of(ModuleCode.PFC, "FALLBACK-SOURCE", "The source version or the new version number is no longer what was requested; reject and request again.");
        }

        return plan;
    }

    private async Task<Result<ProductVersionDecideFallbackResponse>> ApplyAsync(
        FallbackRequestRow row, ProductRow product, ProductVersionRow defective, ProductVersionRow source, FallbackPlan plan,
        string decidedBy, Instant decidedAt, string decisionReason, Instant now, CancellationToken cancellationToken)
    {
        // The new version is a write-once copy of the source's content: same artefact with only version and windows replaced.
        var sourceJson = (await db.Artifacts.AsNoTracking().SingleAsync(a => a.ArtefactHash == source.ArtefactHash, cancellationToken).ConfigureAwait(false)).CanonicalJson;
        var definition = ArtefactCompiler.Parse(sourceJson) with
        {
            Version = plan.NewVersion,
            Windows = new ProductWindows { NewBusiness = plan.NewBusinessWindow, Renewal = plan.NewRenewalWindow },
        };
        var compiled = ArtefactCompiler.Compile(definition);
        if (compiled.IsFailure)
        {
            return compiled.Error;
        }

        var artefact = compiled.Value;
        if (await db.Artifacts.FindAsync([artefact.Hash.Value], cancellationToken).ConfigureAwait(false) is null)
        {
            db.Artifacts.Add(new ArtifactRow { ArtefactHash = artefact.Hash.Value, CanonicalJson = artefact.CanonicalJson, SizeBytes = artefact.SizeBytes, StoredAt = now });
        }

        // Close the defective version's new-business window first (only that end moves; its renewal window and content are untouched),
        // so the exclusion constraint never sees two open windows.
        defective.NewBusinessTo = plan.ClosedWindow.End;
        defective.LifecycleSubstate = Codes.Of(ProductVersionLockedSubstate.ClosedToNewBusiness);
        defective.RecordVersion++;
        if (await FallbackSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } closeError)
        {
            return closeError;
        }

        var state = ProductVersionStateModel.Machine.Start(ProductVersionState.Draft).Value;
        foreach (var trigger in new[] { ProductVersionTrigger.Submit, ProductVersionTrigger.Approve, ProductVersionTrigger.Publish })
        {
            var next = ProductVersionStateModel.Machine.Fire(state, trigger);
            if (next.IsFailure)
            {
                return next.Error;
            }

            state = next.Value;
        }

        var created = db.Versions.Add(new ProductVersionRow
        {
            ProductVersionId = ProductVersionId.New(),
            ProductId = product.ProductId,
            LegalEntityId = source.LegalEntityId,
            Jurisdiction = source.Jurisdiction,
            Major = plan.NewVersion.Major,
            Minor = plan.NewVersion.Minor,
            Status = Codes.Of(state),
            LifecycleSubstate = Codes.Of(ProductVersionLockedSubstate.Active),
            IsAbstract = source.IsAbstract,
            Channels = [.. source.Channels],
            ContractCurrency = source.ContractCurrency,
            NewBusinessFrom = plan.NewBusinessWindow.Start,
            NewBusinessTo = plan.NewBusinessWindow.End,
            RenewalFrom = plan.NewRenewalWindow.Start,
            RenewalTo = plan.NewRenewalWindow.End,
            ArtefactHash = artefact.Hash.Value,
            SchemaVersion = source.SchemaVersion,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            LockedAt = now,
            FallbackOfVersionId = source.ProductVersionId,
            ReplacesVersionId = defective.ProductVersionId,
        }).Entity;

        row.Status = FallbackSupport.Applied;
        row.FallbackDate = plan.NewBusinessWindow.Start;
        row.DecidedBy = decidedBy;
        row.DecidedAt = decidedAt;
        row.DecisionReason = decisionReason;
        row.NewVersionId = created.ProductVersionId;
        row.RecordVersion++;
        if (await FallbackSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ProductVersionPublishedV1.Descriptor),
            "Product",
            product.ProductId.Value.ToString(),
            new ProductVersionPublishedV1
            {
                ProductCode = product.Code,
                Version = plan.NewVersion,
                ArtefactHash = artefact.Hash,
                NewBusinessWindow = FallbackSupport.ToInstants(plan.NewBusinessWindow),
                RenewalWindow = FallbackSupport.ToInstants(plan.NewRenewalWindow),
                Jurisdiction = definition.Jurisdiction,
                LegalEntity = definition.LegalEntity,
                Channels = definition.Channels,
                IpidChanged = false,
                ChangedAreas = [],
                RatingSlotDeclaration = definition.References.Rating,
                FallbackOf = plan.Source.Number,
                Replaces = plan.Defective.Number,
            },
            BusinessKeys.Empty.With("productId", product.ProductId.Value.ToString())));
        return Respond(row, product.Code, plan.Defective.Number);
    }

    private async Task<Result<ProductVersionDecideFallbackResponse>> FinishAsync(
        FallbackRequestRow row, ProductRow product, string status, string decidedBy, Instant decidedAt, string reason, ProductVersionId? newVersion,
        CancellationToken cancellationToken)
    {
        row.Status = status;
        row.DecidedBy = decidedBy;
        row.DecidedAt = decidedAt;
        row.DecisionReason = reason;
        row.NewVersionId = newVersion;
        row.RecordVersion++;
        if (await FallbackSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        var defective = await db.Versions.AsNoTracking().SingleAsync(v => v.ProductVersionId == row.DefectiveVersionId, cancellationToken).ConfigureAwait(false);
        return Respond(row, product.Code, new ProductVersionNumber(defective.Major, defective.Minor));
    }

    private static ProductVersionDecideFallbackResponse Respond(FallbackRequestRow row, string productCode, ProductVersionNumber defective) =>
        new() { Fallback = FallbackSupport.View(row, productCode) with { DefectiveVersion = defective } };

    private static DomainError Sod(string detail) => DomainError.Of(ModuleCode.PFC, "SOD", detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SECURITY: fall-back {FallbackId} not executed: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid fallbackId, string reason);
}

internal sealed class DecideFallbackAuditor : ICommandAuditor<DecideFallback, ProductVersionDecideFallbackResponse>
{
    public CommandAuditFacts Describe(DecideFallback command, Result<ProductVersionDecideFallbackResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("fallbackId", command.Request.FallbackId.ToString("D"));
        var subject = FallbackSupport.Subject(command.Request.FallbackId);
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        var view = ok.Value.Fallback;
        return new CommandAuditFacts
        {
            ObjectRef = subject,
            ObjectNumber = $"{view.ProductCode} {view.DefectiveVersion}",
            BusinessKeys = keys.With("productCode", view.ProductCode),
            Changes = AuditDiff.Compute(
                new { status = FallbackSupport.Pending },
                new
                {
                    status = view.Status.ToString(),
                    decision = command.Request.Decision.ToString(),
                    newVersion = view.NewVersion?.ToString(),
                    decidedBy = view.DecidedBy,
                }),
        };
    }
}
