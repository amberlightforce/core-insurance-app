using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary><c>ri.Contract.create</c> as a command of the platform pipeline.</summary>
internal sealed record CreateContract(ContractCreateRequest Request) : ICommand<ContractCreateResponse>;

/// <summary>Shape rules (anything the business rules would also catch is left to <see cref="ContractRules"/>).</summary>
internal sealed class CreateContractValidator : AbstractValidator<CreateContract>
{
    public CreateContractValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.LegalEntity).NotEmpty().MaximumLength(64).When(c => c.Request is not null);
        RuleFor(c => c.Request.ContractYear).InclusiveBetween(1990, 2200).When(c => c.Request is not null);
        RuleFor(c => c.Request.Scope).NotNull().When(c => c.Request is not null);
        RuleFor(c => c.Request.Clause).NotNull().When(c => c.Request is not null);
        RuleFor(c => c.Request.Layers).NotNull().When(c => c.Request is not null);
        RuleFor(c => c.Request.Participations).NotNull().When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Creates a Draft treaty (REQ-RI-030..032, -037, -038, -046, -047, -231): the gapless <c>RI_CONTRACT</c> number per
/// legal entity, the stable treaty id (the first contract's number) with the contract year, and one version with its
/// section, layers, clause and panel. XoL per risk and EUR only (D-SL4-04); signed lines add up to the placed share
/// with exactly one lead; reinsurers and brokers are organisation parties (PTY contract).
/// </summary>
internal sealed class CreateContractHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    INumberingService numbering,
    ILegalEntityDirectory legalEntities,
    PartyDirectory parties,
    IOptions<ReinsuranceOptions> options) : ICommandHandler<CreateContract, ContractCreateResponse>
{
    public async Task<Result<ContractCreateResponse>> HandleAsync(CreateContract command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        if (!string.Equals(request.LegalEntity, legalEntityCode.Value, StringComparison.Ordinal))
        {
            return RiErrors.Validation("legalEntity", "LEGAL_ENTITY_MISMATCH", "A contract is created in the caller's own legal entity.");
        }

        var content = ContractInput.From(request);
        if (content.IsFailure)
        {
            return content.Error!;
        }

        if (ContractRules.Validate(content.Value) is { } invalid)
        {
            return invalid;
        }

        if (await parties.CheckAsync(content.Value.Lines, cancellationToken).ConfigureAwait(false) is { } partyError)
        {
            return partyError;
        }

        var legalEntity = legalEntities.Resolve(legalEntityCode);
        var jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value;
        var zone = options.Value.Zone;
        var now = clock.Now;
        var number = await numbering.NextAsync(new NumberRequest(ReinsuranceModule.ContractSeries, now.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);

        var contractId = RiContractId.New();
        var actor = context.Actor.ToString();
        var contract = new ContractRow
        {
            ContractId = contractId,
            LegalEntityId = legalEntity,
            Jurisdiction = jurisdiction,
            ContractNumber = number.Value,
            StableTreatyId = number.Value,
            ContractType = content.Value.ContractType,
            ContractYear = content.Value.ContractYear,
            Currency = content.Value.Currency,
            Status = ContractStateModel.Code(ContractStateModel.Machine.Start(ContractStatus.Draft).Value),
            Participants = [.. ContractSupport.ActorKeys(context)],
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
        };
        var version = new ContractVersionRow
        {
            VersionId = Guid.CreateVersion7(),
            ContractId = contractId,
            VersionNo = 1,
            ValidFrom = content.Value.ValidFrom,
            ValidTo = content.Value.ValidTo,
            KnownFrom = now,
            ContentRev = 1,
            PlacedPct = content.Value.PlacedPct,
            ContentHash = content.Value.Hash(contractId, 1).Value,
            CreatedAt = now,
            CreatedBy = actor,
        };
        db.Contracts.Add(contract);
        db.Versions.Add(version);
        ContractSupport.AddContent(db, version, 1, content.Value);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ContractSupport.IsUniqueViolation(ex, "ux_contract_treaty_year"))
        {
            return RiErrors.Validation("contractYear", "TREATY_YEAR_EXISTS", "The treaty already has a contract for this year.");
        }

        return new ContractCreateResponse { Contract = ContractSupport.ToView(contract, version, content.Value, legalEntityCode.Value, zone) };
    }
}

/// <summary>Audit facts of <c>ri.Contract.create</c>.</summary>
internal sealed class CreateContractAuditor : ICommandAuditor<CreateContract, ContractCreateResponse>
{
    public CommandAuditFacts Describe(CreateContract command, Result<ContractCreateResponse>? result)
    {
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts();
        }

        var contract = ok.Value.Contract;
        return new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.RI, "Contract", contract.ContractId),
            ObjectNumber = contract.ContractNumber,
            BusinessKeys = BusinessKeys.Empty.With("riContractId", contract.ContractId.Value.ToString("D")),
            Changes = AuditDiff.Compute(null, new
            {
                status = "DRAFT",
                contractType = contract.ContractType.ToString(),
                contractYear = contract.ContractYear,
                placedPct = contract.PlacedPct,
                period = contract.Period.ToString(),
                layers = contract.Layers.Count,
                participations = contract.Participations.Count,
            }),
        };
    }
}
