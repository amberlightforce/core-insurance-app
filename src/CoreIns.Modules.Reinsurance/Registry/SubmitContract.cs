using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary><c>ri.Contract.submit</c> as a command of the platform pipeline.</summary>
internal sealed record SubmitContract(ContractSubmitRequest Request) : ICommand<ContractSubmitResponse>;

/// <summary>Shape rules.</summary>
internal sealed class SubmitContractValidator : AbstractValidator<SubmitContract>
{
    public SubmitContractValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
    }
}

/// <summary>
/// Draft → PendingApproval (REQ-RI-056, -057). The content is read back from the stored rows, validated again (the
/// panel adds up, the parties are still usable) and hashed; RI itself then creates the PLT approval request in-process
/// for type <c>RI.CONTRACT_APPROVE</c>, subject = this contract, hash = the content hash (PITFALLS 3, 4). No client
/// input sets the type, subject, authority or referral role. The submitter joins the contract's participants and so can
/// never approve it (PITFALLS 5).
/// </summary>
internal sealed class SubmitContractHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    PartyDirectory parties,
    IPlatformApprovalService approvals,
    IOptions<ReinsuranceOptions> options) : ICommandHandler<SubmitContract, ContractSubmitResponse>
{
    public async Task<Result<ContractSubmitResponse>> HandleAsync(SubmitContract command, CancellationToken cancellationToken)
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
        var pending = ContractStateModel.Machine.Fire(status, ContractTrigger.Submit);
        if (pending.IsFailure)
        {
            return RiErrors.State(status, "submitted");
        }

        var version = await ContractSupport.CurrentVersionAsync(db, contract.ContractId, cancellationToken).ConfigureAwait(false);
        var content = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
        if (ContractRules.Validate(content) is { } invalid)
        {
            return invalid;
        }

        if (await parties.CheckAsync(content.Lines, cancellationToken).ConfigureAwait(false) is { } partyError)
        {
            return partyError;
        }

        var hash = content.Hash(contract.ContractId, version.VersionNo);
        if (!string.Equals(hash.Value, version.ContentHash, StringComparison.Ordinal))
        {
            return RiErrors.Stale("The stored content does not match its content hash.");
        }

        Platform.Contracts.Api.ApprovalRequestResponse approval;
        try
        {
            approval = await approvals.RequestAsync(ContractLifecycleService.BuildRequest(contract, version, content, hash), CommandOptions.New(), cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        var now = clock.Now;
        var zone = options.Value.Zone;
        var actor = context.Actor.ToString();
        contract.Status = ContractStateModel.Code(pending.Value);
        contract.SubmittedBy = actor;
        contract.SubmittedAt = now;
        contract.ApprovalRequestId = approval.Request.RequestId;
        contract.ReturnReason = null;
        contract.Participants = [.. contract.Participants.Concat(ContractSupport.ActorKeys(context)).Distinct(StringComparer.Ordinal)];
        contract.UpdatedAt = now;
        contract.RecordVersion++;
        version.ApprovalRequestId = approval.Request.RequestId;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The contract changed meanwhile.");
        }

        return new ContractSubmitResponse
        {
            Contract = ContractSupport.ToView(contract, version, content, legalEntityCode.Value, zone),
            ApprovalRequestId = new ApprovalRequestId(approval.Request.RequestId),
        };
    }
}

/// <summary>Audit facts of <c>ri.Contract.submit</c>.</summary>
internal sealed class SubmitContractAuditor : ICommandAuditor<SubmitContract, ContractSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitContract command, Result<ContractSubmitResponse>? result)
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
            Changes = AuditDiff.Compute(new { status = "DRAFT" }, new { status = "PENDING_APPROVAL", approvalRequestId = ok.Value.ApprovalRequestId.Value }),
        };
    }
}
