using System.Text.Json;
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
using CoreIns.Platform.Contracts.Common;
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
/// Shared pieces of the release lifecycle (BO-PFC-04, REQ-PFC-186, -204, -206, -211..-213): submit a change set, sign it off with
/// maker-checker (PLT approval PFC.Release, authority PFC.RELEASE), schedule it, publish it on its activation date.
/// The server derives everything the approval binds from stored rows; the client names the change set and a decision.
/// </summary>
internal static class ReleaseSupport
{
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Returned = "RETURNED";
    public const string Scheduled = "SCHEDULED";
    public const string Published = "PUBLISHED";
    public const string Environment = "PRODUCTION";

    public static ObjectRef Subject(Guid releaseId) => new(ModuleCode.PFC, ProductAuthorityTypes.ReleaseSubjectType, releaseId.ToString("D"));

    /// <summary>The frozen content PLT approves: who owns it, which change set, which number and the compiled candidate.</summary>
    public static Sha256Hash ContentHash(string legalEntity, string jurisdiction, string productCode, Guid changeSetId, ProductVersionNumber version, string candidateArtefactHash) =>
        CanonicalJson.HashOf(
            new
            {
                type = ProductAuthorityTypes.ReleaseApprovalType,
                legalEntity,
                jurisdiction,
                product = productCode,
                changeSetId = changeSetId.ToString("D"),
                version = version.ToString(),
                candidateArtefactHash,
            },
            SharedKernelJson.Options);

    public static DomainError Sod(string detail) => DomainError.Of(ModuleCode.PFC, "SOD", detail);

    public static DomainError StateError(string detail) => DomainError.Of(ModuleCode.PFC, "RELEASE-STATE", detail);

    public static DomainError ChangeSetNotFound() => DomainError.Of(ModuleCode.PFC, "CHANGE-SET-NOT-FOUND", "The change set does not exist.");

    public static ReleaseSchedule Schedule(ReleaseRequestRow row, string productCode, string status) => new()
    {
        ReleaseId = row.ReleaseId,
        ProductCode = productCode,
        Version = new ProductVersionNumber(row.NewMajor, row.NewMinor),
        Status = status switch
        {
            Published => ReleaseSchedule.StatusValue.Published,
            Scheduled => ReleaseSchedule.StatusValue.Scheduled,
            _ => ReleaseSchedule.StatusValue.Cancelled,
        },
        ActivationDate = (row.ActivationDate ?? default).ToString(),
        NewBusinessFrom = (row.NewBusinessFrom ?? default).ToString(),
        NewBusinessTo = row.NewBusinessTo?.ToString(),
        RenewalFrom = (row.RenewalFrom ?? default).ToString(),
        RenewalTo = row.RenewalTo?.ToString(),
    };

    /// <summary>Loads the change set of the caller's legal entity and its release in the given status, both locked.</summary>
    public static async Task<(ChangeSetRow? ChangeSet, ReleaseRequestRow? Release)> LoadAsync(
        ProductDbContext db, Guid changeSetId, LegalEntityId legalEntity, string releaseStatus, CancellationToken cancellationToken)
    {
        var changeSet = await db.ChangeSets
            .FromSql($"SELECT * FROM pfc.change_set WHERE change_set_id = {changeSetId} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (changeSet is null)
        {
            return (null, null);
        }

        var release = await db.Releases
            .FromSql($"SELECT * FROM pfc.release_request WHERE change_set_id = {changeSetId} AND status = {releaseStatus} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return (changeSet, release);
    }

    /// <summary>Re-derives the bound hash from the stored draft; a changed draft or row is a tampered release.</summary>
    public static Result<ProductArtefact> VerifyBound(ReleaseRequestRow release, ChangeSetRow changeSet, ProductRow product, string legalEntityCode)
    {
        var draft = ChangeSetSupport.ParseDraft(changeSet.DraftJson);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var compiled = ArtefactCompiler.Compile(draft.Value);
        var number = new ProductVersionNumber(release.NewMajor, release.NewMinor);
        if (compiled.IsFailure
            || compiled.Value.Hash.Value != release.CandidateArtefactHash
            || draft.Value.Version != number
            || ContentHash(legalEntityCode, product.Jurisdiction, product.Code, changeSet.ChangeSetId, number, release.CandidateArtefactHash).Value != release.PayloadHash)
        {
            return Sod("The release no longer matches the content that was submitted for approval.");
        }

        return draft.Value;
    }

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
            return DomainError.Of(ModuleCode.PFC, "RELEASE-STATE", "A release or version with this number was created meanwhile; reload and retry.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.PFC, "STALE", "The change set changed meanwhile; reload and retry.");
        }
    }

    public static CommandAuditFacts Facts(Guid changeSetId, object? before, object? after, string? number = null) => new()
    {
        ObjectRef = ChangeSetSupport.Subject(changeSetId),
        ObjectNumber = number,
        BusinessKeys = BusinessKeys.Empty.With("changeSetId", changeSetId.ToString("D")),
        Changes = after is null ? [] : AuditDiff.Compute(before, after),
    };
}

/// <summary>
/// Publishes a scheduled release: creates the version through the import path (lint, compile, windows, predecessor window closing,
/// Locked, <c>ProductVersionPublishedV1</c>), then marks the release PUBLISHED. Shared by schedule (activation today) and the due-date job.
/// </summary>
internal sealed class ReleasePublisher(ProductDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities, IEventPublisher events)
{
    public async Task<Result<ProductVersionRow>> PublishAsync(ReleaseRequestRow release, CancellationToken cancellationToken)
    {
        var changeSet = await db.ChangeSets.SingleAsync(c => c.ChangeSetId == release.ChangeSetId, cancellationToken).ConfigureAwait(false);
        var draft = ChangeSetSupport.ParseDraft(changeSet.DraftJson);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        // Only the windows differ from the approved candidate (the schedule sets them); everything else is the approved content.
        var definition = draft.Value with
        {
            Windows = new ProductWindows
            {
                NewBusiness = new DateRange(release.NewBusinessFrom!.Value, release.NewBusinessTo),
                Renewal = new DateRange(release.RenewalFrom!.Value, release.RenewalTo),
            },
        };
        var importer = new ImportProductVersionHandler(db, context, clock, legalEntities, events);
        var imported = await importer.HandleAsync(new ImportProductVersion(new ProductVersionImportRequest { Definition = definition, Lock = true }), cancellationToken).ConfigureAwait(false);
        if (imported.IsFailure)
        {
            return imported.Error;
        }

        var version = await db.Versions.SingleAsync(
            v => v.ProductId == release.ProductId && v.Major == release.NewMajor && v.Minor == release.NewMinor, cancellationToken).ConfigureAwait(false);
        release.Status = ReleaseSupport.Published;
        release.PublishedVersionId = version.ProductVersionId;
        release.PublishedAt = clock.Now;
        release.RecordVersion++;
        if (await ReleaseSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        return version;
    }
}

/// <summary><c>pfc.ProductVersion.submit</c>: the draft must lint clean and keep its identity; opens the PLT approval (REQ-PFC-186).</summary>
internal sealed record SubmitChangeSet(ProductVersionSubmitRequest Request) : ICommand<ProductVersionSubmitResponse>;

internal sealed class SubmitChangeSetValidator : AbstractValidator<SubmitChangeSet>
{
    public SubmitChangeSetValidator()
    {
        RuleFor(c => c.Request.ChangeSetId).NotEmpty();
        RuleFor(c => c.Request.RecordVersion).GreaterThanOrEqualTo(1);
    }
}

internal sealed class SubmitChangeSetHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IPlatformApprovalService approvals,
    IEventPublisher events) : ICommandHandler<SubmitChangeSet, ProductVersionSubmitResponse>
{
    public async Task<Result<ProductVersionSubmitResponse>> HandleAsync(SubmitChangeSet command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);

        var changeSet = await db.ChangeSets
            .FromSql($"SELECT * FROM pfc.change_set WHERE change_set_id = {request.ChangeSetId} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (changeSet is null)
        {
            return ReleaseSupport.ChangeSetNotFound();
        }

        if (changeSet.Status != ChangeSetSupport.Open)
        {
            return DomainError.Of(ModuleCode.PFC, "CHANGE-SET-STATE", $"The change set is {changeSet.Status}; only an open change set can be submitted.");
        }

        if (changeSet.RecordVersion != request.RecordVersion)
        {
            return DomainError.Of(ModuleCode.PFC, "STALE", "The change set changed meanwhile; reload and retry.");
        }

        var product = await db.Products.FromSql($"SELECT * FROM pfc.product WHERE product_id = {changeSet.ProductId.Value} FOR UPDATE")
            .AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var parsed = ChangeSetSupport.ParseDraft(changeSet.DraftJson);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var draft = parsed.Value;
        var validation = ChangeSetSupport.Validation(draft);
        if (!validation.Clean && !context.DryRun)
        {
            return ChecksFailed(validation);
        }

        var identity = await CheckIdentityAsync(changeSet, product, draft, caller.Value, cancellationToken).ConfigureAwait(false);
        if (identity is not null)
        {
            return identity;
        }

        var view = await ChangeSetSupport.ViewAsync(db, changeSet, product.Code, cancellationToken).ConfigureAwait(false);
        if (view.IsFailure)
        {
            return view.Error;
        }

        if (context.DryRun)
        {
            return new ProductVersionSubmitResponse
            {
                DryRun = true,
                ChangeSetState = view.Value.ChangeSetState,
                Validation = validation,
                Version = draft.Version,
                ReleaseId = null,
                ApprovalRequestId = null,
            };
        }

        var compiled = ArtefactCompiler.Compile(draft);
        if (compiled.IsFailure)
        {
            return compiled.Error;
        }

        var candidateHash = compiled.Value.Hash.Value;
        var releaseId = EntityIds.NewGuid();
        var hash = ReleaseSupport.ContentHash(caller.Value, product.Jurisdiction, product.Code, changeSet.ChangeSetId, draft.Version, candidateHash);
        var maker = FallbackSupport.ActorKey(context.Actor);
        ApprovalRequestResponse approval;
        try
        {
            approval = await approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = ProductAuthorityTypes.ReleaseApprovalType,
                    ObjectRef = ReleaseSupport.Subject(releaseId),
                    PayloadHash = hash,
                    Diff = JsonSerializer.SerializeToElement(new { legalEntityId = legalEntity.Value, changeSetId = changeSet.ChangeSetId }, SharedKernelJson.Options),
                    Editors = context.OnBehalfOf is { } principal ? [maker, FallbackSupport.ActorKey(principal)] : [maker],
                    Authority = new ApprovalAuthority
                    {
                        Type = ProductAuthorityTypes.Release.Value,
                        Codes = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [ProductAuthorityTypes.ProductLineDimension] = product.LineCode,
                            [ProductAuthorityTypes.JurisdictionDimension] = product.Jurisdiction,
                        },
                    },
                    ReferralRole = ProductAuthorityTypes.CheckerRole,
                    Reason = $"Release of {product.Code} {draft.Version}.",
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        var now = clock.Now;
        db.Releases.Add(new ReleaseRequestRow
        {
            ReleaseId = releaseId,
            LegalEntityId = legalEntity,
            Jurisdiction = product.Jurisdiction,
            ProductId = product.ProductId,
            ChangeSetId = changeSet.ChangeSetId,
            NewMajor = draft.Version.Major,
            NewMinor = draft.Version.Minor,
            Status = ReleaseSupport.Submitted,
            PayloadHash = hash.Value,
            CandidateArtefactHash = candidateHash,
            ApprovalRequestId = approval.Request.RequestId,
            SubmittedBy = maker,
            SubmittedByPrincipal = context.OnBehalfOf is { } p ? FallbackSupport.ActorKey(p) : null,
            SubmittedAt = now,
            RecordVersion = 1,
        });
        changeSet.Status = "SUBMITTED";
        changeSet.RecordVersion++;
        changeSet.UpdatedAt = now;
        changeSet.UpdatedBy = context.Actor.ToString();
        if (await ReleaseSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ProductVersionSubmittedV1.Descriptor),
            "Product",
            product.ProductId.Value.ToString(),
            new ProductVersionSubmittedV1
            {
                ProductCode = product.Code,
                Version = draft.Version,
                CandidateArtefactHash = compiled.Value.Hash,
                ChangeClasses = ["CONTENT"],
                SignificantAdaptation = false,
                RequiredSignOffRoles = [ProductAuthorityTypes.CheckerRole],
                ChangeSetId = changeSet.ChangeSetId,
                RatingSlotDeclaration = draft.References.Rating,
            },
            BusinessKeys.Empty.With("productId", product.ProductId.Value.ToString()).With("changeSetId", changeSet.ChangeSetId.ToString("D"))));

        var submitted = await ChangeSetSupport.ViewAsync(db, changeSet, product.Code, cancellationToken).ConfigureAwait(false);
        return submitted.IsFailure
            ? submitted.Error
            : new ProductVersionSubmitResponse
            {
                DryRun = false,
                ChangeSetState = submitted.Value.ChangeSetState,
                Validation = validation,
                Version = draft.Version,
                ReleaseId = releaseId,
                ApprovalRequestId = new ApprovalRequestId(approval.Request.RequestId),
            };
    }

    /// <summary>The identity fields are editable in the draft; at submit they must still name this product and a new, higher version.</summary>
    private async Task<DomainError?> CheckIdentityAsync(ChangeSetRow changeSet, ProductRow product, ProductArtefact draft, string legalEntityCode, CancellationToken cancellationToken)
    {
        var baseVersion = await db.Versions.AsNoTracking().Where(v => v.ProductVersionId == changeSet.BaseVersionId)
            .Select(v => new { v.Major, v.Minor, v.SchemaVersion }).SingleAsync(cancellationToken).ConfigureAwait(false);
        if (draft.Product.Code != product.Code)
        {
            return Identity("product.code", "The product code cannot change; it identifies the product.");
        }

        if (draft.LegalEntity != legalEntityCode || draft.Jurisdiction != product.Jurisdiction)
        {
            return Identity("legalEntity", "The legal entity and jurisdiction cannot change.");
        }

        if (draft.ProductLine.Code != product.LineCode)
        {
            return Identity("productLine.code", "The product line cannot change.");
        }

        if (draft.SchemaVersion != baseVersion.SchemaVersion)
        {
            return Identity("schemaVersion", "The schema version cannot change in a change set.");
        }

        var highest = await db.Versions.AsNoTracking().Where(v => v.ProductId == product.ProductId)
            .Select(v => new ProductVersionNumber(v.Major, v.Minor)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var live = await db.Releases.AsNoTracking()
            .Where(r => r.ProductId == product.ProductId && (r.Status == ReleaseSupport.Submitted || r.Status == ReleaseSupport.Approved || r.Status == ReleaseSupport.Scheduled))
            .Select(r => new ProductVersionNumber(r.NewMajor, r.NewMinor)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var top = highest.Concat(live).DefaultIfEmpty(new ProductVersionNumber(0, 0)).Max();
        if (draft.Version <= top)
        {
            return Identity("version", $"The version must be higher than every existing or pending version ({top}); edit /version in the draft.");
        }

        return null;
    }

    private static DomainError Identity(string path, string message) =>
        new(ErrorCode.For(ModuleCode.PFC, "CHECKS-FAILED"), message) { FieldErrors = [new FieldError(path, "PFC-RELEASE-IDENTITY", "pfc.release.identity", message)] };

    private static DomainError ChecksFailed(ChangeSetValidation validation) =>
        new(ErrorCode.For(ModuleCode.PFC, "CHECKS-FAILED"), "The draft has lint findings; fix them before submitting.")
        {
            FieldErrors = [.. validation.Findings.Select(f => new FieldError(f.Path, f.Code, f.MessageKey, f.Message))],
        };
}

internal sealed class SubmitChangeSetAuditor : ICommandAuditor<SubmitChangeSet, ProductVersionSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitChangeSet command, Result<ProductVersionSubmitResponse>? result) =>
        ReleaseSupport.Facts(
            command.Request.ChangeSetId,
            new { status = ChangeSetSupport.Open },
            result is { IsSuccess: true } ok && !ok.Value.DryRun ? new { status = "SUBMITTED", version = ok.Value.Version.ToString(), releaseId = ok.Value.ReleaseId?.ToString("D") } : null);
}

/// <summary>
/// <c>pfc.ProductVersion.signOff</c> (REQ-PFC-204, -206): the checker approves or returns a submitted change set. Order: lock and re-derive the
/// bound hash; the checker is neither the submitter nor the submitter's principal and is a person; PLT decides (authority PFC.RELEASE, SoD, hash);
/// on APPROVE <c>verifyForExecution</c> binds subject, type and hash. A request already decided in the generic PLT inbox converges here.
/// </summary>
internal sealed record SignOffRelease(ProductVersionSignOffRequest Request) : ICommand<ProductVersionSignOffResponse>;

internal sealed class SignOffReleaseValidator : AbstractValidator<SignOffRelease>
{
    public SignOffReleaseValidator()
    {
        RuleFor(c => c.Request.ChangeSetId).NotEmpty();
        RuleFor(c => c.Request.Comment).NotEmpty().MaximumLength(500).WithErrorCode("COMMENT");
    }
}

internal sealed partial class SignOffReleaseHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IPlatformApprovalService approvals,
    IEventPublisher events,
    ILogger<SignOffReleaseHandler> logger) : ICommandHandler<SignOffRelease, ProductVersionSignOffResponse>
{
    public async Task<Result<ProductVersionSignOffResponse>> HandleAsync(SignOffRelease command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);

        var (changeSet, release) = await ReleaseSupport.LoadAsync(db, request.ChangeSetId, legalEntity, ReleaseSupport.Submitted, cancellationToken).ConfigureAwait(false);
        if (changeSet is null)
        {
            return ReleaseSupport.ChangeSetNotFound();
        }

        if (release is null)
        {
            return ReleaseSupport.StateError("The change set has no release waiting for sign-off.");
        }

        var product = await db.Products.FromSql($"SELECT * FROM pfc.product WHERE product_id = {release.ProductId.Value} FOR UPDATE")
            .AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);

        // 1. The stored content must still be what the approval was requested for.
        var bound = ReleaseSupport.VerifyBound(release, changeSet, product, caller.Value);
        if (bound.IsFailure)
        {
            LogRefused(logger, release.ReleaseId, "stored content no longer matches the bound hash");
            return bound.Error;
        }

        // 2. Four eyes across delegation, and a person decides.
        var callerKeys = context.OnBehalfOf is { } principal
            ? new[] { FallbackSupport.ActorKey(context.Actor), FallbackSupport.ActorKey(principal) }
            : [FallbackSupport.ActorKey(context.Actor)];
        var makerKeys = release.SubmittedByPrincipal is null ? new[] { release.SubmittedBy } : [release.SubmittedBy, release.SubmittedByPrincipal];
        if (callerKeys.Any(k => makerKeys.Contains(k, StringComparer.Ordinal)))
        {
            return DomainError.Of(ModuleCode.PLT, "SELF-APPROVAL", "The submitter cannot sign off their own release.");
        }

        if (context.Actor.Kind != ActorKind.User)
        {
            return DomainError.Of(ModuleCode.PLT, "CHECKER-MUST-BE-HUMAN", "Only a person can sign off a release.");
        }

        var approve = request.Decision == ProductVersionSignOffRequest.DecisionValue.Approve;
        var subject = ReleaseSupport.Subject(release.ReleaseId);
        var hash = Sha256Hash.Parse(release.PayloadHash);

        // 3. The PLT request must be this release's (type, subject, hash).
        ApprovalGetResponse plt;
        try
        {
            plt = await approvals.GetAsync(release.ApprovalRequestId.ToString("D"), cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        if (plt.Request.Type != ProductAuthorityTypes.ReleaseApprovalType || plt.Request.ObjectRef != subject || plt.Request.PayloadHash != hash)
        {
            LogRefused(logger, release.ReleaseId, "the PLT request is not bound to this release");
            return ReleaseSupport.Sod("The approval request is not bound to this release.");
        }

        string decidedBy;
        var decidedAt = clock.Now;
        var comment = request.Comment;
        switch (plt.Request.Status)
        {
            case ApprovalStatus.PendingApproval:
                try
                {
                    var decided = await approvals.DecideAsync(
                        new ApprovalDecideRequest
                        {
                            RequestId = release.ApprovalRequestId,
                            Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject,
                            PayloadHash = hash,
                            Comment = request.Comment,
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
                    return ReleaseSupport.StateError("The approval request was already approved; confirm it with APPROVE.");
                }

                decidedBy = $"{d.Checker.Kind.ToString().ToUpperInvariant()}:{d.Checker.Id}";
                decidedAt = d.DecidedAt;
                comment = d.Comment ?? request.Comment;
                break;

            default:
                // Rejected, withdrawn or expired in PLT: the release is returned; an APPROVE call cannot resurrect it.
                if (approve)
                {
                    return ReleaseSupport.StateError($"The approval request is {plt.Request.Status}; the release cannot be approved.");
                }

                decidedBy = plt.Decision is { } r ? $"{r.Checker.Kind.ToString().ToUpperInvariant()}:{r.Checker.Id}" : FallbackSupport.ActorKey(context.Actor);
                decidedAt = plt.Decision?.DecidedAt ?? decidedAt;
                comment = plt.Decision?.Comment ?? request.Comment;
                break;
        }

        if (approve)
        {
            // 4. The approval must verify for exactly this subject, type and hash, under the authority PFC computes itself.
            ApprovalVerifyForExecutionResponse verified;
            try
            {
                verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest
                    {
                        RequestId = release.ApprovalRequestId,
                        Hash = hash,
                        Type = ProductAuthorityTypes.ReleaseApprovalType,
                        ObjectRef = subject,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
            {
                LogRefused(logger, release.ReleaseId, ex.Error.Code.Value);
                return ReleaseSupport.Sod($"PLT refused the sign-off ({ex.Error.Code}).");
            }

            if (!verified.Ok
                || verified.Authority.Type != ProductAuthorityTypes.Release.Value
                || verified.Authority.Codes?.GetValueOrDefault(ProductAuthorityTypes.ProductLineDimension) != product.LineCode
                || verified.Authority.Codes?.GetValueOrDefault(ProductAuthorityTypes.JurisdictionDimension) != product.Jurisdiction)
            {
                LogRefused(logger, release.ReleaseId, "the decided authority does not match");
                return ReleaseSupport.Sod("The approval is not decided under the release authority for this product line and jurisdiction.");
            }
        }

        release.Status = approve ? ReleaseSupport.Approved : ReleaseSupport.Returned;
        release.DecidedBy = decidedBy;
        release.DecidedAt = decidedAt;
        release.DecisionComment = comment;
        release.RecordVersion++;
        var now = clock.Now;
        if (!approve)
        {
            // A returned draft is editable again so the author can fix it and resubmit.
            changeSet.Status = ChangeSetSupport.Open;
            changeSet.RecordVersion++;
            changeSet.UpdatedAt = now;
            changeSet.UpdatedBy = context.Actor.ToString();
        }

        if (await ReleaseSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        var number = new ProductVersionNumber(release.NewMajor, release.NewMinor);
        var rating = bound.Value.References.Rating;
        if (approve)
        {
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ProductVersionApprovedV1.Descriptor),
                "Product",
                product.ProductId.Value.ToString(),
                new ProductVersionApprovedV1
                {
                    ProductCode = product.Code,
                    Version = number,
                    ArtefactHash = Sha256Hash.Parse(release.CandidateArtefactHash),
                    Approvals = [new Approval { Role = ProductAuthorityTypes.CheckerRole, DecidedAt = decidedAt }],
                    EvidencePackId = release.ReleaseId,
                    RatingSlotDeclaration = rating,
                },
                BusinessKeys.Empty.With("productId", product.ProductId.Value.ToString()).With("evidencePackId", release.ReleaseId.ToString("D"))));
        }
        else
        {
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ProductVersionReturnedV1.Descriptor),
                "Product",
                product.ProductId.Value.ToString(),
                new ProductVersionReturnedV1
                {
                    ProductCode = product.Code,
                    Version = number,
                    ReturnedByRole = ProductAuthorityTypes.CheckerRole,
                    ReasonCategory = "REVIEW_COMMENT",
                    RatingSlotDeclaration = rating,
                },
                BusinessKeys.Empty.With("productId", product.ProductId.Value.ToString())));
        }

        var view = await ChangeSetSupport.ViewAsync(db, changeSet, product.Code, cancellationToken).ConfigureAwait(false);
        return view.IsFailure
            ? view.Error
            : new ProductVersionSignOffResponse
            {
                ApprovalState = approve ? ProductVersionSignOffResponse.ApprovalStateValue.Approved : ProductVersionSignOffResponse.ApprovalStateValue.Returned,
                ChangeSetState = view.Value.ChangeSetState,
                ReleaseId = release.ReleaseId,
                Version = number,
            };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SECURITY: release {ReleaseId} not signed off: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid releaseId, string reason);
}

internal sealed class SignOffReleaseAuditor : ICommandAuditor<SignOffRelease, ProductVersionSignOffResponse>
{
    public CommandAuditFacts Describe(SignOffRelease command, Result<ProductVersionSignOffResponse>? result) =>
        ReleaseSupport.Facts(
            command.Request.ChangeSetId,
            new { status = ReleaseSupport.Submitted },
            result is { IsSuccess: true } ok ? new { status = ok.Value.ApprovalState.ToString(), decision = command.Request.Decision.ToString(), version = ok.Value.Version.ToString() } : null);
}

/// <summary><c>pfc.ProductVersion.schedule</c> (REQ-PFC-211, -212): sets the windows of an approved release; today publishes at once.</summary>
internal sealed record ScheduleRelease(ProductVersionScheduleRequest Request) : ICommand<ProductVersionScheduleResponse>;

internal sealed class ScheduleReleaseValidator : AbstractValidator<ScheduleRelease>
{
    public ScheduleReleaseValidator()
    {
        RuleFor(c => c.Request.ChangeSetId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500).WithErrorCode("REASON");
        RuleFor(c => c.Request.ActivationDate).Must(IsDate).WithErrorCode("DATE");
        RuleFor(c => c.Request.NewBusinessTo).Must(IsDate).When(c => c.Request.NewBusinessTo is not null).WithErrorCode("DATE");
        RuleFor(c => c.Request.RenewalFrom).Must(IsDate).When(c => c.Request.RenewalFrom is not null).WithErrorCode("DATE");
        RuleFor(c => c.Request.RenewalTo).Must(IsDate).When(c => c.Request.RenewalTo is not null).WithErrorCode("DATE");
    }

    private static bool IsDate(string? text) => BusinessDate.TryParse(text, out _);
}

internal sealed class ScheduleReleaseHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    ReleasePublisher publisher,
    IEventPublisher events) : ICommandHandler<ScheduleRelease, ProductVersionScheduleResponse>
{
    public async Task<Result<ProductVersionScheduleResponse>> HandleAsync(ScheduleRelease command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);

        var (changeSet, release) = await ReleaseSupport.LoadAsync(db, request.ChangeSetId, legalEntity, ReleaseSupport.Approved, cancellationToken).ConfigureAwait(false);
        if (changeSet is null)
        {
            return ReleaseSupport.ChangeSetNotFound();
        }

        if (release is null)
        {
            return ReleaseSupport.StateError("The change set has no approved release to schedule.");
        }

        var product = await db.Products.FromSql($"SELECT * FROM pfc.product WHERE product_id = {release.ProductId.Value} FOR UPDATE")
            .AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var bound = ReleaseSupport.VerifyBound(release, changeSet, product, caller.Value);
        if (bound.IsFailure)
        {
            return bound.Error;
        }

        var now = clock.Now;
        var today = FallbackPlanner.DateIn(now, legalEntities.TimeZoneOf(caller));
        var activation = BusinessDate.Parse(request.ActivationDate);
        var renewalFrom = request.RenewalFrom is null ? activation : BusinessDate.Parse(request.RenewalFrom);
        BusinessDate? newBusinessTo = request.NewBusinessTo is null ? null : BusinessDate.Parse(request.NewBusinessTo);
        BusinessDate? renewalTo = request.RenewalTo is null ? null : BusinessDate.Parse(request.RenewalTo);
        if (activation < today)
        {
            return InvalidWindow("activationDate", "The activation date cannot be in the past.");
        }

        if (newBusinessTo is { } nbTo && nbTo <= activation)
        {
            return InvalidWindow("newBusinessTo", "The new-business window must end after it starts.");
        }

        if (renewalTo is { } rnTo && rnTo <= renewalFrom)
        {
            return InvalidWindow("renewalTo", "The renewal window must end after it starts.");
        }

        var window = new DateRange(activation, newBusinessTo);
        var overlap = await CheckOverlapAsync(release, window, cancellationToken).ConfigureAwait(false);
        if (overlap is not null)
        {
            return overlap;
        }

        release.Status = ReleaseSupport.Scheduled;
        release.ActivationDate = activation;
        release.NewBusinessFrom = activation;
        release.NewBusinessTo = newBusinessTo;
        release.RenewalFrom = renewalFrom;
        release.RenewalTo = renewalTo;
        release.ScheduledBy = FallbackSupport.ActorKey(context.Actor);
        release.ScheduledAt = now;
        release.ScheduleReason = request.Reason;
        release.RecordVersion++;
        if (context.DryRun)
        {
            // The checks passed; nothing is stored. The tracked changes are dropped with the request's transaction.
            var preview = ReleaseSupport.Schedule(release, product.Code, ReleaseSupport.Scheduled);
            db.ChangeTracker.Clear();
            return new ProductVersionScheduleResponse { DryRun = true, Schedule = preview with { ReleaseId = null } };
        }

        if (await ReleaseSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        var zone = legalEntities.TimeZoneOf(caller);
        var rating = bound.Value.References.Rating;
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ProductVersionScheduledV1.Descriptor),
            "Product",
            product.ProductId.Value.ToString(),
            new ProductVersionScheduledV1
            {
                ProductCode = product.Code,
                Version = new ProductVersionNumber(release.NewMajor, release.NewMinor),
                ArtefactHash = Sha256Hash.Parse(release.CandidateArtefactHash),
                Environment = ReleaseSupport.Environment,
                ActivationTime = activation.StartOfDayIn(zone),
                NewBusinessWindow = new InstantRange(activation.StartOfDayIn(zone), newBusinessTo?.StartOfDayIn(zone)),
                RenewalWindow = new InstantRange(renewalFrom.StartOfDayIn(zone), renewalTo?.StartOfDayIn(zone)),
                Reason = request.Reason,
                RatingSlotDeclaration = rating,
            },
            BusinessKeys.Empty.With("productId", product.ProductId.Value.ToString())));

        var status = ReleaseSupport.Scheduled;
        if (activation <= today)
        {
            var published = await publisher.PublishAsync(release, cancellationToken).ConfigureAwait(false);
            if (published.IsFailure)
            {
                return published.Error;
            }

            status = ReleaseSupport.Published;
        }

        return new ProductVersionScheduleResponse { DryRun = false, Schedule = ReleaseSupport.Schedule(release, product.Code, status) };
    }

    /// <summary>
    /// Another Locked version may cover part of the window only when it is an older open-ended one that the publication closes (REQ-PFC-033);
    /// a scheduled release of the same product never may (BR-PFC-001).
    /// </summary>
    private async Task<DomainError?> CheckOverlapAsync(ReleaseRequestRow release, DateRange window, CancellationToken cancellationToken)
    {
        var number = new ProductVersionNumber(release.NewMajor, release.NewMinor);
        var locked = await db.Versions.AsNoTracking()
            .Where(v => v.ProductId == release.ProductId && v.Status == "LOCKED").ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var other in locked.Where(o => new DateRange(o.NewBusinessFrom, o.NewBusinessTo).Overlaps(window)))
        {
            var older = new ProductVersionNumber(other.Major, other.Minor) < number;
            if (!(older && other.NewBusinessTo is null && other.NewBusinessFrom < window.Start))
            {
                return DomainError.Of(ModuleCode.PFC, "WINDOW-OVERLAP", $"Locked version {other.Major}.{other.Minor} already covers part of the new-business window.");
            }
        }

        var scheduled = await db.Releases.AsNoTracking()
            .Where(r => r.ProductId == release.ProductId && r.Status == ReleaseSupport.Scheduled && r.ReleaseId != release.ReleaseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var other in scheduled.Where(o => new DateRange(o.NewBusinessFrom!.Value, o.NewBusinessTo).Overlaps(window)))
        {
            return DomainError.Of(ModuleCode.PFC, "WINDOW-OVERLAP", $"Version {other.NewMajor}.{other.NewMinor} is already scheduled for an overlapping new-business window.");
        }

        return null;
    }

    private static DomainError InvalidWindow(string path, string message) =>
        new(ErrorCode.For(ModuleCode.PFC, "INVALID-WINDOW"), message) { FieldErrors = [new FieldError(path, "PFC-INVALID-WINDOW", "pfc.release.invalid-window", message)] };
}

internal sealed class ScheduleReleaseAuditor : ICommandAuditor<ScheduleRelease, ProductVersionScheduleResponse>
{
    public CommandAuditFacts Describe(ScheduleRelease command, Result<ProductVersionScheduleResponse>? result) =>
        ReleaseSupport.Facts(
            command.Request.ChangeSetId,
            new { status = ReleaseSupport.Approved },
            result is { IsSuccess: true } ok && !ok.Value.DryRun
                ? new { status = ok.Value.Schedule.Status.ToString(), activationDate = ok.Value.Schedule.ActivationDate, newBusinessTo = ok.Value.Schedule.NewBusinessTo }
                : null);
}

/// <summary><c>pfc.ProductVersion.cancelSchedule</c>: a scheduled, not yet published release goes back to approved.</summary>
internal sealed record CancelScheduledRelease(ProductVersionCancelScheduleRequest Request) : ICommand<ProductVersionCancelScheduleResponse>;

internal sealed class CancelScheduledReleaseValidator : AbstractValidator<CancelScheduledRelease>
{
    public CancelScheduledReleaseValidator()
    {
        RuleFor(c => c.Request.ChangeSetId).NotEmpty();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(500).WithErrorCode("REASON");
    }
}

internal sealed class CancelScheduledReleaseHandler(
    ProductDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities) : ICommandHandler<CancelScheduledRelease, ProductVersionCancelScheduleResponse>
{
    public async Task<Result<ProductVersionCancelScheduleResponse>> HandleAsync(CancelScheduledRelease command, CancellationToken cancellationToken)
    {
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);
        var (changeSet, release) = await ReleaseSupport.LoadAsync(db, command.Request.ChangeSetId, legalEntity, ReleaseSupport.Scheduled, cancellationToken).ConfigureAwait(false);
        if (changeSet is null)
        {
            return ReleaseSupport.ChangeSetNotFound();
        }

        if (release is null)
        {
            return ReleaseSupport.StateError("The change set has no scheduled release to cancel (it may be published already).");
        }

        var productCode = await db.Products.AsNoTracking().Where(p => p.ProductId == release.ProductId).Select(p => p.Code).SingleAsync(cancellationToken).ConfigureAwait(false);
        var cancelled = ReleaseSupport.Schedule(release, productCode, "CANCELLED");
        release.Status = ReleaseSupport.Approved;
        release.ActivationDate = null;
        release.NewBusinessFrom = null;
        release.NewBusinessTo = null;
        release.RenewalFrom = null;
        release.RenewalTo = null;
        release.ScheduledBy = null;
        release.ScheduledAt = null;
        release.ScheduleReason = null;
        release.RecordVersion++;
        if (await ReleaseSupport.TrySaveAsync(db, cancellationToken).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        return new ProductVersionCancelScheduleResponse { Schedule = cancelled };
    }
}

internal sealed class CancelScheduledReleaseAuditor : ICommandAuditor<CancelScheduledRelease, ProductVersionCancelScheduleResponse>
{
    public CommandAuditFacts Describe(CancelScheduledRelease command, Result<ProductVersionCancelScheduleResponse>? result) =>
        ReleaseSupport.Facts(
            command.Request.ChangeSetId,
            new { status = ReleaseSupport.Scheduled },
            result is { IsSuccess: true } ? new { status = ReleaseSupport.Approved, reason = command.Request.Reason } : null);
}

/// <summary>The due-date step (REQ-PFC-212): publishes one scheduled release whose activation date has come. At-least-once safe: the row is re-checked under lock.</summary>
internal sealed record PublishDueRelease(Guid ReleaseId) : ICommand<string>;

internal sealed class PublishDueReleaseHandler(
    ProductDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    ReleasePublisher publisher) : ICommandHandler<PublishDueRelease, string>
{
    public const string Skipped = "SKIPPED";

    public async Task<Result<string>> HandleAsync(PublishDueRelease command, CancellationToken cancellationToken)
    {
        var caller = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(caller);
        var release = await db.Releases
            .FromSql($"SELECT * FROM pfc.release_request WHERE release_id = {command.ReleaseId} AND legal_entity_id = {legalEntity.Value} AND status = 'SCHEDULED' FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (release is null || release.ActivationDate > FallbackPlanner.DateIn(clock.Now, legalEntities.TimeZoneOf(caller)))
        {
            return Skipped;
        }

        // Product row lock serialises this publication with fall-backs and other releases of the product.
        await db.Products.FromSql($"SELECT * FROM pfc.product WHERE product_id = {release.ProductId.Value} FOR UPDATE").AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var published = await publisher.PublishAsync(release, cancellationToken).ConfigureAwait(false);
        return published.IsFailure ? published.Error : ReleaseSupport.Published;
    }
}
