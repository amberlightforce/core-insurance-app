using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary><c>ri.Contract.endorse</c> as a command of the platform pipeline.</summary>
internal sealed record EndorseContract(ContractEndorseRequest Request) : ICommand<ContractEndorseResponse>;

/// <summary>Shape rules: the reason is required.</summary>
internal sealed class EndorseContractValidator : AbstractValidator<EndorseContract>
{
    public EndorseContractValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Endorses an Active contract from an effective date (BO-RI-03, REQ-RI-031..065). The endorsement is a new
/// <c>ri.contract_version</c> row (valid from the effective date to the treaty's end, recorded now, PENDING) with the
/// in-force content edited as the request says, and a PLT approval request of the same type as the original approval
/// (type, subject, hash and authority are set here, never by the client). Nothing already approved is changed; the
/// version counts once <see cref="ApproveEndorsementHandler"/> has the checker's decision. One endorsement is pending at a
/// time, and its effective date is after the start of the version it replaces.
/// </summary>
internal sealed class EndorseContractHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    PartyDirectory parties,
    IPlatformApprovalService approvals,
    IOptions<ReinsuranceOptions> options) : ICommandHandler<EndorseContract, ContractEndorseResponse>
{
    public async Task<Result<ContractEndorseResponse>> HandleAsync(EndorseContract command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var contract = await ContractSupport.LockAsync(db, legalEntities.Resolve(legalEntityCode), request.ContractId, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        if (contract.RecordVersion != request.ExpectedRecordVersion)
        {
            return RiErrors.Stale(contract.RecordVersion);
        }

        var status = ContractStateModel.Parse(contract.Status);
        if (status != ContractStatus.Active)
        {
            return RiErrors.State(status, "endorsed");
        }

        if (await db.Versions.AnyAsync(v => v.ContractId == contract.ContractId && v.EndorsementStatus == "PENDING", cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.RI, "STATE", "Another endorsement of this contract is still pending approval.");
        }

        var latest = await ContractVersions.LatestAsync(db, contract.ContractId, cancellationToken).ConfigureAwait(false);
        var effective = request.EffectiveFrom;
        if (effective <= latest.ValidFrom || effective >= latest.ValidTo)
        {
            return RiErrors.Validation("effectiveFrom", "EFFECTIVE_FROM_RANGE", $"The effective date must be after {latest.ValidFrom} and before {latest.ValidTo}.");
        }

        var current = await ContractSupport.ReadContentAsync(db, contract, latest, cancellationToken).ConfigureAwait(false);
        var edited = ContractInput.Apply(current, new ContractUpdateRequest
        {
            ExpectedRecordVersion = request.ExpectedRecordVersion,
            Scope = request.Scope, Clause = request.Clause, Layers = request.Layers, Participations = request.Participations, PlacedPct = request.PlacedPct,
        });
        if (edited.IsFailure)
        {
            return edited.Error!;
        }

        // The contract year is checked against the treaty's own year, whatever date the endorsement takes effect.
        if (ContractRules.Validate(edited.Value with { ValidFrom = new BusinessDate(contract.ContractYear, 1, 1) }) is { } invalid)
        {
            return invalid;
        }

        if (await parties.CheckAsync(edited.Value.Lines, cancellationToken).ConfigureAwait(false) is { } partyError)
        {
            return partyError;
        }

        if (edited.Value.Hash(contract.ContractId, 0).Value == current.Hash(contract.ContractId, 0).Value)
        {
            return RiErrors.Validation("reason", "NOTHING_TO_ENDORSE", "The endorsement changes nothing.");
        }

        var content = edited.Value with { ValidFrom = effective };
        var now = clock.Now;
        var versionNo = await db.Versions.Where(v => v.ContractId == contract.ContractId).MaxAsync(v => v.VersionNo, cancellationToken).ConfigureAwait(false) + 1;
        var hash = content.Hash(contract.ContractId, versionNo);
        var actor = context.Actor.ToString();
        var version = new ContractVersionRow
        {
            VersionId = Guid.CreateVersion7(), ContractId = contract.ContractId, VersionNo = versionNo, ValidFrom = effective, ValidTo = latest.ValidTo,
            KnownFrom = now, ContentRev = 1, PlacedPct = content.PlacedPct, ContentHash = hash.Value, CreatedAt = now, CreatedBy = actor,
            EndorsementStatus = "PENDING", EndorsementReason = request.Reason,
        };

        var approvalRequest = ContractLifecycleService.BuildRequest(contract, version, content, hash) with
        {
            Editors = [.. contract.Participants.Concat(ContractSupport.ActorKeys(context)).Distinct(StringComparer.Ordinal)],
            Reason = $"Reinsurance contract {contract.ContractNumber} endorsed from {effective}: {request.Reason}",
        };
        Platform.Contracts.Api.ApprovalRequestResponse approval;
        try
        {
            approval = await approvals.RequestAsync(approvalRequest, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        version.ApprovalRequestId = approval.Request.RequestId;
        try
        {
            // The version first: the content guard accepts child rows only of an existing version's current revision.
            db.Versions.Add(version);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ContractSupport.AddContent(db, version, version.ContentRev, content);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ContractSupport.IsUniqueViolation(ex, "ux_contract_version_no"))
        {
            return RiErrors.Stale("The contract changed meanwhile.");
        }

        return new ContractEndorseResponse
        {
            Contract = ContractSupport.ToView(contract, version, content, legalEntityCode.Value, options.Value.Zone),
            ApprovalRequestId = new ApprovalRequestId(approval.Request.RequestId),
        };
    }
}

/// <summary>Audit facts of <c>ri.Contract.endorse</c>.</summary>
internal sealed class EndorseContractAuditor : ICommandAuditor<EndorseContract, ContractEndorseResponse>
{
    public CommandAuditFacts Describe(EndorseContract command, Result<ContractEndorseResponse>? result)
    {
        var subject = ObjectRef.For(ModuleCode.RI, "Contract", command.Request.ContractId);
        var keys = BusinessKeys.Empty.With("riContractId", command.Request.ContractId.Value.ToString("D"));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = subject,
            ObjectNumber = ok.Value.Contract.ContractNumber,
            BusinessKeys = keys.With("approvalRequestId", ok.Value.ApprovalRequestId.Value.ToString("D")),
            Changes = AuditDiff.Compute(
                new { versionNo = ok.Value.Contract.VersionNo - 1 },
                new { versionNo = ok.Value.Contract.VersionNo, effectiveFrom = command.Request.EffectiveFrom.ToString(), status = "PENDING", reason = command.Request.Reason }),
        };
    }
}

/// <summary><c>ri.Contract.approveEndorsement</c> (APPROVE or REJECT) as a command of the platform pipeline.</summary>
internal sealed record ApproveEndorsement(ContractApproveEndorsementRequest Request) : ICommand<ContractApproveEndorsementResponse>;

/// <summary>Shape rules: a rejection needs its reason.</summary>
internal sealed class ApproveEndorsementValidator : AbstractValidator<ApproveEndorsement>
{
    public ApproveEndorsementValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.VersionNo).GreaterThanOrEqualTo(2).When(c => c.Request is not null);
        RuleFor(c => c.Request.Decision).IsInEnum().When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().WithErrorCode("REASON_REQUIRED")
            .When(c => c.Request is { Decision: ContractApproveEndorsementRequest.DecisionValue.Reject });
    }
}

/// <summary>
/// The checker's decision on a pending endorsement. The endorser (and anyone who took part in the contract) cannot decide
/// it (<c>RI-ERR-SOD</c>); PLT decides on the hash recomputed from the stored rows and checks the checker's
/// <c>RI.CONTRACT_APPROVE</c> authority; the database proves the decision again when the version is approved. An approved
/// endorsement counts from this instant (its record time).
/// </summary>
internal sealed class ApproveEndorsementHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IPlatformApprovalService approvals,
    IOptions<ReinsuranceOptions> options) : ICommandHandler<ApproveEndorsement, ContractApproveEndorsementResponse>
{
    public async Task<Result<ContractApproveEndorsementResponse>> HandleAsync(ApproveEndorsement command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var contract = await ContractSupport.LockAsync(db, legalEntities.Resolve(legalEntityCode), request.ContractId, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        var version = await db.Versions.SingleOrDefaultAsync(v => v.ContractId == contract.ContractId && v.VersionNo == request.VersionNo, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return RiErrors.NotFound();
        }

        if (version.EndorsementStatus != "PENDING" || version.ApprovalRequestId is not { } requestId)
        {
            return DomainError.Of(ModuleCode.RI, "STATE", $"Version {version.VersionNo} is not a pending endorsement.");
        }

        var keys = ContractSupport.ActorKeys(context);
        if (keys.Any(k => contract.Participants.Contains(k, StringComparer.Ordinal) || string.Equals(version.CreatedBy, k, StringComparison.Ordinal)))
        {
            return RiErrors.Sod("The person who entered or endorsed the contract cannot decide the endorsement (REQ-RI-057).");
        }

        var content = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
        var hash = content.Hash(contract.ContractId, version.VersionNo);
        if (!string.Equals(hash.Value, version.ContentHash, StringComparison.Ordinal))
        {
            return RiErrors.Stale("The stored content differs from the content that was submitted for approval.");
        }

        var reject = request.Decision == ContractApproveEndorsementRequest.DecisionValue.Reject;
        var decided = await DecideAsync(contract, version, requestId, hash, reject, request.Reason, cancellationToken).ConfigureAwait(false);
        if (decided.IsFailure)
        {
            return decided.Error!;
        }

        if (!reject && await VerifyAsync(contract, version, requestId, hash, cancellationToken).ConfigureAwait(false) is { } mismatch)
        {
            return mismatch;
        }

        var now = clock.Now;
        if (reject)
        {
            version.EndorsementStatus = "REJECTED";
        }
        else
        {
            version.EndorsementStatus = "APPROVED";
            version.ApprovedAt = now;
            version.ApprovedBy = decided.Value;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The contract changed meanwhile.");
        }

        return new ContractApproveEndorsementResponse
        {
            Contract = ContractSupport.ToView(contract, version, content, legalEntityCode.Value, options.Value.Zone),
            Decision = reject ? ContractApproveEndorsementResponse.DecisionValue.Reject : ContractApproveEndorsementResponse.DecisionValue.Approve,
        };
    }

    /// <summary>PLT confirms (<c>verifyForExecution</c>) that its approved request is for exactly this type, subject, hash and the authority RI asked for.</summary>
    private async Task<DomainError?> VerifyAsync(ContractRow contract, ContractVersionRow version, Guid requestId, Sha256Hash hash, CancellationToken cancellationToken)
    {
        ApprovalVerifyForExecutionResponse verified;
        try
        {
            verified = await approvals.VerifyForExecutionAsync(
                new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = ContractLifecycleService.ApprovalType, ObjectRef = ContractLifecycleService.SubjectOf(contract.ContractId) },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return RiErrors.Stale($"PLT refused execution ({ex.Error.Code}).");
        }

        if (!verified.Ok)
        {
            return RiErrors.Stale($"Approval request {requestId} is {verified.Status}.");
        }

        var authority = verified.Authority;
        return authority.Type == Authority.ReinsuranceAuthorityTypes.ContractApprove.Value
            && authority.Codes?.GetValueOrDefault(Authority.ReinsuranceAuthorityTypes.ContractTypeDimension) == contract.ContractType
            ? null
            : RiErrors.Stale("The approved authority does not match the contract.");
    }

    /// <summary>
    /// PLT decides the request the endorsement is bound to, with the hash RI computed from the stored rows; the result is
    /// the key of the person who decided (the caller). A request already decided in the PLT inbox is accepted only for
    /// exactly this hash and a checker who took no part in the contract or the endorsement.
    /// </summary>
    private async Task<Result<string>> DecideAsync(
        ContractRow contract, ContractVersionRow version, Guid requestId, Sha256Hash hash, bool reject, string? comment, CancellationToken cancellationToken)
    {
        var self = context.Actor.ToString();
        var decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve;
        try
        {
            var current = await approvals.GetAsync(requestId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (current.Request.Status != ApprovalStatus.PendingApproval)
            {
                if (reject)
                {
                    return self;
                }

                if (current.Request.Status != ApprovalStatus.Approved || current.Request.PayloadHash != hash || current.Decision is not { } decided)
                {
                    return RiErrors.Stale($"The approval request is {current.Request.Status}; endorse the contract again.");
                }

                var checker = $"{decided.Checker.Kind.ToString().ToUpperInvariant()}:{decided.Checker.Id}";
                return contract.Participants.Contains(checker, StringComparer.Ordinal) || string.Equals(version.CreatedBy, checker, StringComparison.Ordinal)
                    ? RiErrors.Sod("The approval in the PLT inbox was given by someone who took part in the contract (REQ-RI-057).")
                    : checker;
            }

            await approvals.DecideAsync(
                new ApprovalDecideRequest { RequestId = requestId, Decision = decision, PayloadHash = hash, Comment = comment },
                CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            return self;
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error.Code.Name switch
            {
                "SELF-APPROVAL" or "EDITOR-CANNOT-APPROVE" => RiErrors.Sod("PLT refused: the checker took part in the content (REQ-PLT-115)."),
                "APPROVAL-STALE" or "APPROVAL-HASH-MISMATCH" or "NOT-FOUND" => RiErrors.Stale("The approval request changed or no longer matches the endorsement."),
                _ => ex.Error,
            };
        }
    }
}

/// <summary>Audit facts of <c>ri.Contract.approveEndorsement</c>.</summary>
internal sealed class ApproveEndorsementAuditor : ICommandAuditor<ApproveEndorsement, ContractApproveEndorsementResponse>
{
    public CommandAuditFacts Describe(ApproveEndorsement command, Result<ContractApproveEndorsementResponse>? result)
    {
        var subject = ObjectRef.For(ModuleCode.RI, "Contract", command.Request.ContractId);
        var keys = BusinessKeys.Empty.With("riContractId", command.Request.ContractId.Value.ToString("D"));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        return new CommandAuditFacts
        {
            ObjectRef = subject,
            ObjectNumber = ok.Value.Contract.ContractNumber,
            BusinessKeys = keys,
            Changes = AuditDiff.Compute(
                new { versionNo = command.Request.VersionNo, status = "PENDING" },
                new { versionNo = command.Request.VersionNo, status = ok.Value.Decision == ContractApproveEndorsementResponse.DecisionValue.Reject ? "REJECTED" : "APPROVED", reason = command.Request.Reason }),
        };
    }
}
