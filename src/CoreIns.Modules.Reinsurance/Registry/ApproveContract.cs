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

/// <summary><c>ri.Contract.approve</c> (APPROVE or RETURN) as a command of the platform pipeline.</summary>
internal sealed record ApproveContract(ContractApproveRequest Request) : ICommand<ContractApproveResponse>;

/// <summary>Shape rules: a return needs its reason (REQ-RI-056).</summary>
internal sealed class ApproveContractValidator : AbstractValidator<ApproveContract>
{
    public ApproveContractValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
        RuleFor(c => c.Request.Decision).IsInEnum().When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).NotEmpty().WithErrorCode("REASON_REQUIRED")
            .When(c => c.Request is { Decision: ContractApproveRequest.DecisionValue.Return });
    }
}

/// <summary>
/// The checker's decision on a PendingApproval treaty (REQ-RI-057, -058). The same four-eyes chain as every other
/// module, in order:
/// <list type="number">
/// <item>the contract exists in the caller's legal entity and the caller saw the current version (else <c>RI-ERR-STALE</c>,
/// so of two racing deciders exactly one wins and the other gets 409);</item>
/// <item>the contract is PendingApproval (<c>RI-ERR-STATE</c>);</item>
/// <item>the caller took no part in the content: not the creator, an editor or the submitter, nor the person an AI or
/// service actor acts for (<c>RI-ERR-SOD</c>, PITFALLS 5). PLT repeats the check on its request (its maker and
/// editors), and checks the caller's <c>RI.CONTRACT_APPROVE</c> authority (PITFALLS 1..3);</item>
/// <item>the content recomputed from the stored rows is the content that was submitted; PLT decides on that hash;
/// <c>verifyForExecution</c> then confirms type, subject, hash and the authority decided under;</item>
/// <item>only then the version is sealed (an approved version is immutable, a trigger refuses any change) and, when the
/// period has started (Athens date), the contract becomes Active in the same transaction and
/// <c>RIContractActivated</c> is published; otherwise the scanner activates it at the period start.</item>
/// </list>
/// A RETURN records PLT's rejection with the reason and moves the contract back to Draft.
/// </summary>
internal sealed class ApproveContractHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    ContractLifecycleService lifecycle,
    IPlatformApprovalService approvals,
    IOptions<ReinsuranceOptions> options) : ICommandHandler<ApproveContract, ContractApproveResponse>
{
    public async Task<Result<ContractApproveResponse>> HandleAsync(ApproveContract command, CancellationToken cancellationToken)
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
        if (status != ContractStatus.PendingApproval || contract.ApprovalRequestId is not { } requestId)
        {
            return RiErrors.State(status, request.Decision == ContractApproveRequest.DecisionValue.Return ? "returned" : "approved");
        }

        var keys = ContractSupport.ActorKeys(context);
        if (keys.Any(k => contract.Participants.Contains(k, StringComparer.Ordinal) || string.Equals(contract.SubmittedBy, k, StringComparison.Ordinal)))
        {
            return RiErrors.Sod("The person who entered, edited or submitted the contract cannot decide it (REQ-RI-057).");
        }

        var version = await ContractSupport.CurrentVersionAsync(db, contract.ContractId, cancellationToken).ConfigureAwait(false);
        var content = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
        var hash = content.Hash(contract.ContractId, version.VersionNo);
        if (!string.Equals(hash.Value, version.ContentHash, StringComparison.Ordinal) || version.ApprovalRequestId != requestId)
        {
            return RiErrors.Stale("The stored content differs from the content that was submitted for approval.");
        }

        return request.Decision == ContractApproveRequest.DecisionValue.Return
            ? await ReturnAsync(contract, version, content, requestId, hash, request, legalEntityCode.Value, cancellationToken).ConfigureAwait(false)
            : await ApproveAsync(contract, version, content, requestId, hash, legalEntityCode.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ContractApproveResponse>> ApproveAsync(
        ContractRow contract, ContractVersionRow version, ContractContent content, Guid requestId, Sha256Hash hash, string legalEntity, CancellationToken cancellationToken)
    {
        if (ContractRules.Validate(content) is not null)
        {
            return RiErrors.Stale("The stored content no longer passes the contract rules.");
        }

        var decided = await DecideAsync(contract, requestId, hash, ApprovalDecideRequest.DecisionValue.Approve, comment: null, cancellationToken).ConfigureAwait(false);
        if (decided.IsFailure)
        {
            return decided.Error!;
        }

        if (await lifecycle.VerifyApprovalAsync(contract, version, content, cancellationToken).ConfigureAwait(false) is { } mismatch)
        {
            return mismatch;
        }

        var now = clock.Now;
        var zone = options.Value.Zone;
        var actor = decided.Value;

        // The version first (the seal trigger needs the contract still PendingApproval), then the header.
        version.ApprovedAt = now;
        version.ApprovedBy = actor;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var approved = ContractStateModel.Machine.Fire(ContractStatus.PendingApproval, ContractTrigger.Approve);
            contract.Status = ContractStateModel.Code(approved.Value);
            contract.DecidedBy = actor;
            contract.DecidedAt = now;
            contract.UpdatedAt = now;
            contract.RecordVersion++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The contract changed meanwhile.");
        }

        // Activation at the period start (REQ-RI-058): when the period has already started on the Athens calendar, in the
        // same transaction; otherwise the scanner does it at the start.
        var today = now.ToBusinessDate(zone);
        if (version.ValidFrom <= today)
        {
            var activated = await lifecycle.ActivateAsync(contract, version, content, now, cancellationToken).ConfigureAwait(false);
            if (activated.IsFailure)
            {
                return activated.Error!;
            }

            // A treaty entered after its period ended (a late registration) is Active only for an instant: it expires in the
            // same transaction, so it never lingers as Active, and still answers for the losses of its period.
            if (version.ValidTo <= today)
            {
                var expired = await lifecycle.ExpireAsync(contract, version, now, cancellationToken).ConfigureAwait(false);
                if (expired.IsFailure)
                {
                    return expired.Error!;
                }
            }
        }

        return new ContractApproveResponse
        {
            Contract = ContractSupport.ToView(contract, version, content, legalEntity, zone),
            Decision = ContractApproveResponse.DecisionValue.Approve,
        };
    }

    private async Task<Result<ContractApproveResponse>> ReturnAsync(
        ContractRow contract, ContractVersionRow version, ContractContent content, Guid requestId, Sha256Hash hash, ContractApproveRequest request, string legalEntity,
        CancellationToken cancellationToken)
    {
        var decided = await DecideAsync(contract, requestId, hash, ApprovalDecideRequest.DecisionValue.Reject, request.Reason, cancellationToken).ConfigureAwait(false);
        if (decided.IsFailure)
        {
            return decided.Error!;
        }

        var now = clock.Now;
        var returned = ContractStateModel.Machine.Fire(ContractStatus.PendingApproval, ContractTrigger.Return);
        contract.Status = ContractStateModel.Code(returned.Value);
        contract.ReturnReason = request.Reason;
        contract.DecidedBy = context.Actor.ToString();
        contract.DecidedAt = now;
        contract.UpdatedAt = now;
        contract.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The contract changed meanwhile.");
        }

        return new ContractApproveResponse
        {
            Contract = ContractSupport.ToView(contract, version, content, legalEntity, options.Value.Zone),
            Decision = ContractApproveResponse.DecisionValue.Return,
        };
    }

    /// <summary>
    /// PLT decides the request the contract is bound to, with the hash RI computed from the stored rows; the result is the
    /// key of the person who decided (the caller). PLT's own refusals are mapped: the maker / editor rules to
    /// <c>RI-ERR-SOD</c> (403), a changed request or hash to <c>RI-ERR-STALE</c> (409); an authority refusal is returned as
    /// PLT raised it. The request can also have been decided in the PLT inbox without RI: an approval there is accepted only
    /// for exactly this hash and a checker who took no part in the contract (the contract is then executed by this call,
    /// still verified with <c>verifyForExecution</c>); a rejected or withdrawn request cannot be approved but a RETURN
    /// takes the contract back to Draft.
    /// </summary>
    private async Task<Result<string>> DecideAsync(
        ContractRow contract, Guid requestId, Sha256Hash hash, ApprovalDecideRequest.DecisionValue decision, string? comment, CancellationToken cancellationToken)
    {
        var self = context.Actor.ToString();
        try
        {
            var current = await approvals.GetAsync(requestId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (current.Request.Status != ApprovalStatus.PendingApproval)
            {
                if (decision == ApprovalDecideRequest.DecisionValue.Reject)
                {
                    return self;
                }

                if (current.Request.Status != ApprovalStatus.Approved || current.Request.PayloadHash != hash || current.Decision is not { } decided)
                {
                    return RiErrors.Stale($"The approval request is {current.Request.Status}; return the contract to Draft and submit it again.");
                }

                var checker = $"{decided.Checker.Kind.ToString().ToUpperInvariant()}:{decided.Checker.Id}";
                return contract.Participants.Contains(checker, StringComparer.Ordinal)
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
                "APPROVAL-STALE" or "APPROVAL-HASH-MISMATCH" or "NOT-FOUND" => RiErrors.Stale("The approval request changed or no longer matches the contract."),
                _ => ex.Error,
            };
        }
    }
}

/// <summary>Audit facts of <c>ri.Contract.approve</c>.</summary>
internal sealed class ApproveContractAuditor : ICommandAuditor<ApproveContract, ContractApproveResponse>
{
    public CommandAuditFacts Describe(ApproveContract command, Result<ContractApproveResponse>? result)
    {
        var subject = ObjectRef.For(ModuleCode.RI, "Contract", command.Request.ContractId);
        var keys = BusinessKeys.Empty.With("riContractId", command.Request.ContractId.Value.ToString("D"));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        var contract = ok.Value.Contract;
        return new CommandAuditFacts
        {
            ObjectRef = subject,
            ObjectNumber = contract.ContractNumber,
            BusinessKeys = contract.ApprovalRequestId is { } id ? keys.With("approvalRequestId", id.Value.ToString("D")) : keys,
            Changes = AuditDiff.Compute(
                new { status = "PENDING_APPROVAL" },
                new { status = contract.Status.ToString(), decision = ok.Value.Decision.ToString(), reason = command.Request.Reason }),
        };
    }
}
