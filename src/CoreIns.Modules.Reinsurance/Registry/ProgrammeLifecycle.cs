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

/// <summary><c>ri.Programme.create</c>.</summary>
internal sealed record CreateProgramme(ProgrammeCreateRequest Request) : ICommand<ProgrammeCreateResponse>;

/// <summary><c>ri.Programme.update</c>; the programme id comes from the route.</summary>
internal sealed record UpdateProgramme(RiProgrammeId ProgrammeId, ProgrammeUpdateRequest Request) : ICommand<ProgrammeUpdateResponse>;

/// <summary><c>ri.Programme.renew</c>.</summary>
internal sealed record RenewProgramme(ProgrammeRenewRequest Request) : ICommand<ProgrammeRenewResponse>;

/// <summary>Shape rules of create (the business rules are <see cref="ProgrammeRules"/>).</summary>
internal sealed class CreateProgrammeValidator : AbstractValidator<CreateProgramme>
{
    public CreateProgrammeValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Layers).NotNull().When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>Shape rules of update.</summary>
internal sealed class UpdateProgrammeValidator : AbstractValidator<UpdateProgramme>
{
    public UpdateProgrammeValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>Shape rules of renew.</summary>
internal sealed class RenewProgrammeValidator : AbstractValidator<RenewProgramme>
{
    public RenewProgrammeValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
    }
}

/// <summary>
/// Creates a Draft programme, version 1 (BO-RI-01): the layer ladder is contiguous without overlap, every layer is carried by a contract of the
/// legal entity that is in force for the period and has that layer, and no other programme of the line covers part of the period. The gate is
/// the programme code per year (unique) and the line lock, so two racing creates cannot both pass the overlap check.
/// </summary>
internal sealed class CreateProgrammeHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities) : ICommandHandler<CreateProgramme, ProgrammeCreateResponse>
{
    public async Task<Result<ProgrammeCreateResponse>> HandleAsync(CreateProgramme command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var layers = ProgrammeSupport.Layers(request.Layers);
        if (layers.IsFailure)
        {
            return layers.Error!;
        }

        if (request.Period.End is not { } end)
        {
            return RiErrors.Validation("period", "PERIOD_END_REQUIRED", "A programme period has an end date.");
        }

        var content = new ProgrammeContent(request.Name?.Trim() ?? string.Empty, request.Period.Start, end, layers.Value);
        if (ProgrammeRules.Validate(request.Code, request.LineCode, content, checkIdentity: true) is { } invalid)
        {
            return invalid;
        }

        var legalEntity = legalEntities.Resolve(legalEntityCode);
        await ProgrammeSupport.LockLineAsync(db, legalEntity, request.LineCode, cancellationToken).ConfigureAwait(false);
        if (await ProgrammeSupport.CheckOverlapAsync(db, legalEntity, request.LineCode, content.ValidFrom, content.ValidTo, except: null, cancellationToken).ConfigureAwait(false) is { } overlap)
        {
            return overlap;
        }

        if (await ProgrammeSupport.CheckContractsAsync(db, legalEntity, content, request.LineCode, except: null, cancellationToken).ConfigureAwait(false) is { } contractError)
        {
            return contractError;
        }

        var now = clock.Now;
        var actor = context.Actor.ToString();
        var programme = new ProgrammeRow
        {
            ProgrammeId = RiProgrammeId.New(),
            LegalEntityId = legalEntity,
            Jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value,
            Code = request.Code,
            LineCode = request.LineCode,
            ProgrammeYear = content.ValidFrom.Year,
            Currency = ContractRules.Eur,
            CurrentVersionNo = 1,
            Participants = [.. ContractSupport.ActorKeys(context)],
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
        };
        var version = NewVersion(programme.ProgrammeId, 1, content, now, actor, request.Reason);
        db.Programmes.Add(programme);
        db.ProgrammeVersions.Add(version);
        ProgrammeSupport.AddLayers(db, version, content);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ProgrammeSupport.IsUnique(ex, "ux_programme_code_year"))
        {
            return RiErrors.Validation("code", "PROGRAMME_YEAR_EXISTS", "A programme with this code already exists for the year.");
        }

        var view = (await ProgrammeSupport.ViewsAsync(db, legalEntityCode.Value, [programme], cancellationToken).ConfigureAwait(false))[0];
        return new ProgrammeCreateResponse
        {
            Programme = view,
            Diff = [.. ProgrammeRules.Diff(null, content).Select(d => new ProgrammeCreateResponse.DiffItem { Field = d.Field, Before = d.Before, After = d.After })],
        };
    }

    internal static ProgrammeVersionRow NewVersion(RiProgrammeId programmeId, int versionNo, ProgrammeContent content, Instant now, string actor, string? reason) => new()
    {
        VersionId = Guid.CreateVersion7(),
        ProgrammeId = programmeId,
        VersionNo = versionNo,
        Status = ProgrammeStatuses.Draft,
        Name = content.Name,
        ValidFrom = content.ValidFrom,
        ValidTo = content.ValidTo,
        CreatedAt = now,
        CreatedBy = actor,
        Reason = reason,
    };
}

/// <summary>
/// Edits a programme. The caller must have read the current record version (else <c>RI-ERR-STALE</c>). A DRAFT version is edited in place; a
/// programme that is active is never edited: the update starts a new DRAFT version and the old one stays in force until the new one is approved
/// (BO-RI-02). A version waiting for its decision cannot be edited. The edit passes the rules of a new programme.
/// </summary>
internal sealed class UpdateProgrammeHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities) : ICommandHandler<UpdateProgramme, ProgrammeUpdateResponse>
{
    public async Task<Result<ProgrammeUpdateResponse>> HandleAsync(UpdateProgramme command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(legalEntityCode);
        var programme = await ProgrammeSupport.LockAsync(db, legalEntity, command.ProgrammeId, cancellationToken).ConfigureAwait(false);
        if (programme is null)
        {
            return ProgrammeSupport.NotFound();
        }

        if (programme.RecordVersion != request.ExpectedRecordVersion)
        {
            return RiErrors.Stale(programme.RecordVersion);
        }

        var version = await ProgrammeSupport.CurrentVersionAsync(db, programme, cancellationToken).ConfigureAwait(false);
        if (version.Status == ProgrammeStatuses.PendingApproval)
        {
            return DomainError.Of(ModuleCode.RI, "STATE", "The programme is waiting for its decision; it cannot be edited.");
        }

        if (version.Status is ProgrammeStatuses.Superseded or ProgrammeStatuses.Expired)
        {
            return DomainError.Of(ModuleCode.RI, "STATE", $"The programme is {version.Status}; it cannot be edited.");
        }

        var current = await ProgrammeSupport.ReadContentAsync(db, version, cancellationToken).ConfigureAwait(false);
        var edited = current;
        if (request.Name is { } name)
        {
            edited = edited with { Name = name.Trim() };
        }

        if (request.Period is { } period)
        {
            if (period.End is not { } end)
            {
                return RiErrors.Validation("period", "PERIOD_END_REQUIRED", "A programme period has an end date.");
            }

            edited = edited with { ValidFrom = period.Start, ValidTo = end };
        }

        if (request.Layers is { } requested)
        {
            var layers = ProgrammeSupport.Layers(requested);
            if (layers.IsFailure)
            {
                return layers.Error!;
            }

            edited = edited with { Layers = layers.Value };
        }

        if (ProgrammeRules.Validate(programme.Code, programme.LineCode, edited, checkIdentity: false) is { } invalid)
        {
            return invalid;
        }

        if (edited.ValidFrom.Year != programme.ProgrammeYear)
        {
            return RiErrors.Validation("period", "PROGRAMME_YEAR_MISMATCH", "The period stays in the programme year; renew the programme for another year.");
        }

        await ProgrammeSupport.LockLineAsync(db, legalEntity, programme.LineCode, cancellationToken).ConfigureAwait(false);
        if (await ProgrammeSupport.CheckOverlapAsync(db, legalEntity, programme.LineCode, edited.ValidFrom, edited.ValidTo, programme.ProgrammeId, cancellationToken).ConfigureAwait(false) is { } overlap)
        {
            return overlap;
        }

        if (await ProgrammeSupport.CheckContractsAsync(db, legalEntity, edited, programme.LineCode, programme.ProgrammeId, cancellationToken).ConfigureAwait(false) is { } contractError)
        {
            return contractError;
        }

        var now = clock.Now;
        if (version.Status == ProgrammeStatuses.Draft)
        {
            version.Name = edited.Name;
            version.ValidFrom = edited.ValidFrom;
            version.ValidTo = edited.ValidTo;
            version.Reason = request.Reason ?? version.Reason;
            await db.ProgrammeLayers.Where(l => l.VersionId == version.VersionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            ProgrammeSupport.AddLayers(db, version, edited);
        }
        else
        {
            // An approved programme is never edited: the change is the next version, in force once it is approved.
            programme.CurrentVersionNo++;
            version = CreateProgrammeHandler.NewVersion(programme.ProgrammeId, programme.CurrentVersionNo, edited, now, context.Actor.ToString(), request.Reason);
            db.ProgrammeVersions.Add(version);
            ProgrammeSupport.AddLayers(db, version, edited);
        }

        programme.Participants = [.. programme.Participants.Concat(ContractSupport.ActorKeys(context)).Distinct(StringComparer.Ordinal)];
        programme.UpdatedAt = now;
        programme.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RiErrors.Stale("The programme changed meanwhile.");
        }

        var view = (await ProgrammeSupport.ViewsAsync(db, legalEntityCode.Value, [programme], cancellationToken).ConfigureAwait(false))[0];
        return new ProgrammeUpdateResponse
        {
            Programme = view,
            Diff = [.. ProgrammeRules.Diff(current, edited).Select(d => new ProgrammeUpdateResponse.DiffItem { Field = d.Field, Before = d.Before, After = d.After })],
        };
    }
}

/// <summary>
/// Renews a programme into the next year (BO-RI-01): a new Draft programme with the same code and line, the period moved by one year and the
/// layers copied; every contract of the ladder is copied as a new Draft contract of the next year (same treaty, layers, clause and panel), numbered
/// from the contract series, that goes through its own approval. The renewer is a maker of the copies, so cannot approve them. A year that
/// already has the programme (or a contract of the treaty) is refused; nothing is written then (one transaction).
/// </summary>
internal sealed class RenewProgrammeHandler(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    INumberingService numbering,
    ILegalEntityDirectory legalEntities,
    IOptions<ReinsuranceOptions> options) : ICommandHandler<RenewProgramme, ProgrammeRenewResponse>
{
    public async Task<Result<ProgrammeRenewResponse>> HandleAsync(RenewProgramme command, CancellationToken cancellationToken)
    {
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(legalEntityCode);
        var source = await ProgrammeSupport.LockAsync(db, legalEntity, command.Request.ProgrammeId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return ProgrammeSupport.NotFound();
        }

        var sourceVersion = await ProgrammeSupport.CurrentVersionAsync(db, source, cancellationToken).ConfigureAwait(false);
        var content = await ProgrammeSupport.ReadContentAsync(db, sourceVersion, cancellationToken).ConfigureAwait(false);
        var year = source.ProgrammeYear + 1;
        if (await db.Programmes.AsNoTracking().AnyAsync(p => p.LegalEntityId == legalEntity && p.Code == source.Code && p.ProgrammeYear == year, cancellationToken).ConfigureAwait(false))
        {
            return RiErrors.Validation("programmeId", "PROGRAMME_YEAR_EXISTS", "A programme with this code already exists for the next year.");
        }

        await ProgrammeSupport.LockLineAsync(db, legalEntity, source.LineCode, cancellationToken).ConfigureAwait(false);
        var from = content.ValidFrom.AddYears(1);
        var to = content.ValidTo.AddYears(1);
        if (await ProgrammeSupport.CheckOverlapAsync(db, legalEntity, source.LineCode, from, to, except: null, cancellationToken).ConfigureAwait(false) is { } overlap)
        {
            return overlap;
        }

        var now = clock.Now;
        var zone = options.Value.Zone;
        var actor = context.Actor.ToString();
        var jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value;
        var copies = new Dictionary<RiContractId, RiContractId>();
        foreach (var oldId in content.ContractIds)
        {
            var copy = await CopyContractAsync(oldId, legalEntity, jurisdiction, now, zone, cancellationToken).ConfigureAwait(false);
            if (copy.IsFailure)
            {
                return copy.Error!;
            }

            copies[oldId] = copy.Value;
        }

        var renewed = content with
        {
            ValidFrom = from,
            ValidTo = to,
            Layers = [.. content.Layers.Select(l => l with { ContractId = copies[l.ContractId] })],
        };
        var programme = new ProgrammeRow
        {
            ProgrammeId = RiProgrammeId.New(),
            LegalEntityId = legalEntity,
            Jurisdiction = jurisdiction,
            Code = source.Code,
            LineCode = source.LineCode,
            ProgrammeYear = year,
            Currency = source.Currency,
            RenewedFromId = source.ProgrammeId,
            CurrentVersionNo = 1,
            Participants = [.. ContractSupport.ActorKeys(context)],
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
        };
        var version = CreateProgrammeHandler.NewVersion(programme.ProgrammeId, 1, renewed, now, actor, command.Request.Reason);
        db.Programmes.Add(programme);
        db.ProgrammeVersions.Add(version);
        ProgrammeSupport.AddLayers(db, version, renewed);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ProgrammeSupport.IsUnique(ex, "ux_programme_code_year"))
        {
            return RiErrors.Validation("programmeId", "PROGRAMME_YEAR_EXISTS", "A programme with this code already exists for the next year.");
        }
        catch (DbUpdateException ex) when (ProgrammeSupport.IsUnique(ex, "ux_contract_treaty_year"))
        {
            return RiErrors.Validation("programmeId", "TREATY_YEAR_EXISTS", "A treaty of the programme already has a contract for the next year.");
        }

        var view = (await ProgrammeSupport.ViewsAsync(db, legalEntityCode.Value, [programme], cancellationToken).ConfigureAwait(false))[0];
        return new ProgrammeRenewResponse
        {
            Programme = view,
            Diff = [.. ProgrammeRules.Diff(content, renewed).Select(d => new ProgrammeRenewResponse.DiffItem { Field = d.Field, Before = d.Before, After = d.After })],
        };
    }

    /// <summary>A new Draft contract of the next year: same treaty, the in-force layers, clause and panel, the treaty period moved by one year.</summary>
    private async Task<Result<RiContractId>> CopyContractAsync(
        RiContractId sourceId, LegalEntityId legalEntity, string jurisdiction, Instant now, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var contract = await db.Contracts.AsNoTracking().SingleAsync(c => c.ContractId == sourceId, cancellationToken).ConfigureAwait(false);
        var first = await db.Versions.AsNoTracking().SingleAsync(v => v.ContractId == sourceId && v.VersionNo == 1, cancellationToken).ConfigureAwait(false);
        var latest = await ContractVersions.LatestAsync(db, sourceId, cancellationToken).ConfigureAwait(false);
        var stored = await ContractSupport.ReadContentAsync(db, contract, latest, cancellationToken).ConfigureAwait(false);
        var content = stored with { ContractYear = contract.ContractYear + 1, ValidFrom = first.ValidFrom.AddYears(1), ValidTo = first.ValidTo.AddYears(1) };
        if (ContractRules.Validate(content) is { } invalid)
        {
            return invalid;
        }

        if (await db.Contracts.AsNoTracking().AnyAsync(c => c.LegalEntityId == legalEntity && c.StableTreatyId == contract.StableTreatyId && c.ContractYear == content.ContractYear, cancellationToken).ConfigureAwait(false))
        {
            return RiErrors.Validation("programmeId", "TREATY_YEAR_EXISTS", "A treaty of the programme already has a contract for the next year.");
        }

        var number = await numbering.NextAsync(new NumberRequest(ReinsuranceModule.ContractSeries, now.ToBusinessDate(zone)), cancellationToken).ConfigureAwait(false);
        var contractId = RiContractId.New();
        var actor = context.Actor.ToString();
        db.Contracts.Add(new ContractRow
        {
            ContractId = contractId,
            LegalEntityId = legalEntity,
            Jurisdiction = jurisdiction,
            ContractNumber = number.Value,
            StableTreatyId = contract.StableTreatyId,
            ContractType = contract.ContractType,
            ContractYear = content.ContractYear,
            Currency = contract.Currency,
            Status = ContractStateModel.Code(ContractStateModel.Machine.Start(ContractStatus.Draft).Value),
            Participants = [.. ContractSupport.ActorKeys(context)],
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
        });
        var version = new ContractVersionRow
        {
            VersionId = Guid.CreateVersion7(),
            ContractId = contractId,
            VersionNo = 1,
            ValidFrom = content.ValidFrom,
            ValidTo = content.ValidTo,
            KnownFrom = now,
            ContentRev = 1,
            PlacedPct = content.PlacedPct,
            ContentHash = content.Hash(contractId, 1).Value,
            CreatedAt = now,
            CreatedBy = actor,
        };
        db.Versions.Add(version);
        ContractSupport.AddContent(db, version, 1, content);
        return contractId;
    }
}

/// <summary>Audit facts of <c>ri.Programme.create</c>.</summary>
internal sealed class CreateProgrammeAuditor : ICommandAuditor<CreateProgramme, ProgrammeCreateResponse>
{
    public CommandAuditFacts Describe(CreateProgramme command, Result<ProgrammeCreateResponse>? result) =>
        result is { IsSuccess: true } ok ? ProgrammeAudit.Facts(ok.Value.Programme, null) : new CommandAuditFacts();
}

/// <summary>Audit facts of <c>ri.Programme.update</c>.</summary>
internal sealed class UpdateProgrammeAuditor : ICommandAuditor<UpdateProgramme, ProgrammeUpdateResponse>
{
    public CommandAuditFacts Describe(UpdateProgramme command, Result<ProgrammeUpdateResponse>? result) =>
        result is { IsSuccess: true } ok
            ? ProgrammeAudit.Facts(ok.Value.Programme, new { recordVersion = command.Request.ExpectedRecordVersion })
            : new CommandAuditFacts
            {
                ObjectRef = ObjectRef.For(ModuleCode.RI, "Programme", command.ProgrammeId),
                BusinessKeys = BusinessKeys.Empty.With("riProgrammeId", command.ProgrammeId.Value.ToString("D")),
            };
}

/// <summary>Audit facts of <c>ri.Programme.renew</c>.</summary>
internal sealed class RenewProgrammeAuditor : ICommandAuditor<RenewProgramme, ProgrammeRenewResponse>
{
    public CommandAuditFacts Describe(RenewProgramme command, Result<ProgrammeRenewResponse>? result) =>
        result is { IsSuccess: true } ok ? ProgrammeAudit.Facts(ok.Value.Programme, null) : new CommandAuditFacts();
}

internal static class ProgrammeAudit
{
    public static CommandAuditFacts Facts(RiProgramme programme, object? before) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.RI, "Programme", programme.ProgrammeId),
        ObjectNumber = programme.Code,
        BusinessKeys = BusinessKeys.Empty.With("riProgrammeId", programme.ProgrammeId.Value.ToString("D")),
        Changes = AuditDiff.Compute(before, new
        {
            status = programme.Status.ToString(),
            recordVersion = programme.RecordVersion,
            versionNo = programme.VersionNo,
            programmeYear = programme.ProgrammeYear,
            period = programme.Period.ToString(),
            layers = programme.Layers.Count,
            contracts = programme.Contracts.Count,
        }),
    };
}
