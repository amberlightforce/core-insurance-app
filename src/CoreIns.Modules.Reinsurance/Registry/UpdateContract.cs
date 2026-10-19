using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary><c>ri.Contract.update</c>: edits a Draft contract; the contract id comes from the route.</summary>
internal sealed record UpdateContract(RiContractId ContractId, ContractUpdateRequest Request) : ICommand<ContractUpdateResponse>;

/// <summary>Shape rules.</summary>
internal sealed class UpdateContractValidator : AbstractValidator<UpdateContract>
{
    public UpdateContractValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Edits a Draft contract (REQ-RI-056). Checks in order: found in the caller's legal entity; the caller read the current
/// version (else <c>RI-ERR-STALE</c>, so a racing editor loses with 409, PITFALLS 15); the contract is Draft (else
/// <c>RI-ERR-STATE</c>: a submitted or approved contract is never edited); the edited content passes the same rules as
/// a new one. An edit adds a new revision of the child rows and keeps the old one as history (PITFALLS 17); the editor
/// joins the contract's participants, so they can never approve it (PITFALLS 5).
/// </summary>
internal sealed class UpdateContractHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    PartyDirectory parties,
    IOptions<ReinsuranceOptions> options) : ICommandHandler<UpdateContract, ContractUpdateResponse>
{
    public async Task<Result<ContractUpdateResponse>> HandleAsync(UpdateContract command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var contract = await ContractSupport.LockAsync(db, legalEntities.Resolve(legalEntityCode), command.ContractId, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        if (contract.RecordVersion != request.ExpectedRecordVersion)
        {
            return RiErrors.Stale(contract.RecordVersion);
        }

        var status = ContractStateModel.Parse(contract.Status);
        if (status != ContractStatus.Draft)
        {
            return RiErrors.State(status, "edited");
        }

        var version = await ContractSupport.CurrentVersionAsync(db, contract.ContractId, cancellationToken).ConfigureAwait(false);
        var current = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
        var edited = ContractInput.Apply(current, request);
        if (edited.IsFailure)
        {
            return edited.Error!;
        }

        var content = edited.Value;
        if (ContractRules.Validate(content) is { } invalid)
        {
            return invalid;
        }

        if (await parties.CheckAsync(content.Lines, cancellationToken).ConfigureAwait(false) is { } partyError)
        {
            return partyError;
        }

        var now = clock.Now;
        var zone = options.Value.Zone;
        version.ContentRev++;
        version.ValidFrom = content.ValidFrom;
        version.ValidTo = content.ValidTo;
        version.PlacedPct = content.PlacedPct;
        version.ContentHash = content.Hash(contract.ContractId, version.VersionNo).Value;
        contract.Participants = [.. contract.Participants.Concat(ContractSupport.ActorKeys(context)).Distinct(StringComparer.Ordinal)];
        contract.UpdatedAt = now;
        contract.RecordVersion++;
        try
        {
            // The version first: the content guard accepts child rows only of the version's current revision.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ContractSupport.AddContent(db, version, version.ContentRev, content);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The contract changed meanwhile.");
        }

        return new ContractUpdateResponse { Contract = ContractSupport.ToView(contract, version, content, legalEntityCode.Value, zone) };
    }
}

/// <summary>Audit facts of <c>ri.Contract.update</c>.</summary>
internal sealed class UpdateContractAuditor : ICommandAuditor<UpdateContract, ContractUpdateResponse>
{
    public CommandAuditFacts Describe(UpdateContract command, Result<ContractUpdateResponse>? result)
    {
        var subject = ObjectRef.For(ModuleCode.RI, "Contract", command.ContractId);
        var keys = BusinessKeys.Empty.With("riContractId", command.ContractId.Value.ToString("D"));
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = subject, BusinessKeys = keys };
        }

        var contract = ok.Value.Contract;
        return new CommandAuditFacts
        {
            ObjectRef = subject,
            ObjectNumber = contract.ContractNumber,
            BusinessKeys = keys,
            Changes = AuditDiff.Compute(
                new { status = "DRAFT", recordVersion = command.Request.ExpectedRecordVersion },
                new
                {
                    status = "DRAFT",
                    recordVersion = contract.RecordVersion,
                    placedPct = contract.PlacedPct,
                    period = contract.Period.ToString(),
                    layers = contract.Layers.Count,
                    participations = contract.Participations.Count,
                    reason = command.Request.Reason,
                }),
        };
    }
}
