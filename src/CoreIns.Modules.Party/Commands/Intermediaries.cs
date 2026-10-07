using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Events;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using DomainStatus = CoreIns.Modules.Party.Domain.IntermediaryStatus;

namespace CoreIns.Modules.Party.Commands;

/// <summary><c>pty.Intermediary.create</c>: an intermediary record for an existing party plus its first producer code.</summary>
internal sealed record CreateIntermediary(IntermediaryCreateRequest Request) : ICommand<IntermediaryCreateResponse>;

/// <summary><c>pty.Intermediary.update</c> (SL-0: the Onboarding → Active transition only).</summary>
internal sealed record UpdateIntermediary(IntermediaryId IntermediaryId, IntermediaryUpdateRequest Request) : ICommand<IntermediaryUpdateResponse>;

internal sealed class CreateIntermediaryValidator : AbstractValidator<CreateIntermediary>
{
    public CreateIntermediaryValidator()
    {
        RuleFor(c => c.Request.IntermediaryType).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
        RuleFor(c => c.Request.Register.RegisterName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.Register.Chamber).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.Register.RegisterNumber).NotEmpty().MaximumLength(50);
        RuleFor(c => c.Request.Register.RegistrationCategory).NotEmpty().Matches("^[A-Z][A-Z0-9_]{0,63}$").WithErrorCode("CODE");
        RuleFor(c => c.Request.Register.VerificationLink).MaximumLength(500);
        RuleFor(c => c.Request.Register.EvidenceRef).MaximumLength(200);
    }
}

internal sealed class UpdateIntermediaryValidator : AbstractValidator<UpdateIntermediary>
{
    public UpdateIntermediaryValidator()
    {
        RuleFor(c => c.Request.ExpectedRecordVersion).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Request.Status).Equal(Contracts.Api.IntermediaryStatus.Active).WithErrorCode("TRANSITION_NOT_OFFERED")
            .WithMessage("SL-0 offers the activation only; suspension and termination arrive with W2-PTY-04.");
    }
}

/// <summary>
/// Creates the intermediary (status Onboarding, REQ-PTY-198) with the register data of a manual verification
/// (REQ-PTY-188, REQ-PTY-189 Greece pack) and issues its producer code through the numbering service (REQ-PTY-204)
/// with its authorities (REQ-PTY-205). Publishes <c>ProducerCodeChanged</c>.
/// </summary>
internal sealed class CreateIntermediaryHandler(
    PartyDbContext db, RequestContext context, IClock clock, INumberingService numbering, IEventPublisher events, PartyProtection protection,
    IntermediaryQueries queries) : ICommandHandler<CreateIntermediary, IntermediaryCreateResponse>
{
    public async Task<Result<IntermediaryCreateResponse>> HandleAsync(CreateIntermediary command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var party = await db.Parties.SingleOrDefaultAsync(p => p.PartyId == request.PartyId && p.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (party is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party does not exist.");
        }

        if (party.Status == Codes.Of(PartyStatus.Merged))
        {
            return DomainError.Of(ModuleCode.PTY, "MERGED", "The party was merged; use the survivor.");
        }

        if (await db.Intermediaries.AnyAsync(i => i.PartyId == request.PartyId && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.PTY, "VALIDATION", "The party is already an intermediary.");
        }

        var now = clock.Now;
        var actor = context.Actor.ToString();
        var intermediary = new IntermediaryRow
        {
            IntermediaryId = IntermediaryId.New(),
            PartyId = request.PartyId,
            LegalEntityId = legalEntity,
            IntermediaryType = request.IntermediaryType,
            Status = Codes.Of(IntermediaryStateModel.Machine.Start(DomainStatus.Onboarding).Value),
            RegisterName = request.Register.RegisterName.Trim(),
            Chamber = request.Register.Chamber.Trim(),
            RegisterNumber = request.Register.RegisterNumber.Trim(),
            RegistrationCategory = request.Register.RegistrationCategory,
            RegistrationDate = request.Register.RegistrationDate,
            RegisterStatus = Codes.Of(request.Register.RegisterStatus),
            VerificationLink = request.Register.VerificationLink,
            EvidenceRef = request.Register.EvidenceRef,
            RegisterVerifiedAt = now,
            ValidFrom = request.ValidFrom,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
        };
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.ProducerCode, request.ValidFrom), cancellationToken).ConfigureAwait(false);
        var code = new ProducerCodeRow
        {
            ProducerCodeId = ProducerCodeId.New(),
            IntermediaryId = intermediary.IntermediaryId,
            LegalEntityId = legalEntity,
            Code = number.Value,
            Status = Codes.Of(ProducerCodeStatus.Active),
            CollectPremium = request.Authorities.CollectPremium,
            IssueCoverNotes = request.Authorities.IssueCoverNotes,
            BindWithinAuthority = request.Authorities.BindWithinAuthority,
            ServiceOnly = request.Authorities.ServiceOnly,
            ValidFrom = request.ValidFrom,
            RecordVersion = 1,
            CreatedAt = now,
            CreatedBy = actor,
        };
        db.Intermediaries.Add(intermediary);
        db.ProducerCodes.Add(code);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ProducerCodeChangedV1.Descriptor),
            "Intermediary",
            intermediary.IntermediaryId.Value.ToString(),
            new ProducerCodeChangedV1
            {
                IntermediaryId = intermediary.IntermediaryId,
                ProducerCode = code.Code,
                Status = code.Status,
                AuthoritiesSummary = IntermediaryQueries.AuthoritiesSummary(code),
                ParentProducerCode = null,
            },
            BusinessKeys.Empty.With("intermediaryId", intermediary.IntermediaryId.Value.ToString())));

        return new IntermediaryCreateResponse { Intermediary = (await queries.ViewAsync(intermediary.IntermediaryId, cancellationToken).ConfigureAwait(false))! };
    }
}

/// <summary>
/// Activates an intermediary (REQ-PTY-198) through the lifecycle state machine with optimistic concurrency
/// (REQ-PTY-043). Guards modelled in SL-0: register status Active (REQ-PTY-189/190). Licence, appointment and agreement
/// guards arrive with their entities in W2-PTY-04 (listed as a deviation). Publishes <c>IntermediaryStatusChanged</c>.
/// </summary>
internal sealed class UpdateIntermediaryHandler(
    PartyDbContext db, RequestContext context, IClock clock, IEventPublisher events, PartyProtection protection, IntermediaryQueries queries)
    : ICommandHandler<UpdateIntermediary, IntermediaryUpdateResponse>
{
    public async Task<Result<IntermediaryUpdateResponse>> HandleAsync(UpdateIntermediary command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var row = await db.Intermediaries.SingleOrDefaultAsync(i => i.IntermediaryId == command.IntermediaryId && i.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The intermediary does not exist.");
        }

        if (row.RecordVersion != command.Request.ExpectedRecordVersion)
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "STALE"), "The intermediary changed meanwhile; reload it.")
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["currentRecordVersion"] = row.RecordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            };
        }

        var from = Codes.Parse<DomainStatus>(row.Status);
        var to = IntermediaryStateModel.Machine.Fire(from, IntermediaryTrigger.Activate);
        if (to.IsFailure)
        {
            return to.Error;
        }

        var missing = new List<string>();
        if (row.RegisterStatus != "ACTIVE")
        {
            missing.Add("REGISTER_INACTIVE");
        }

        if (missing.Count > 0)
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "ACTIVATION-INCOMPLETE"), "The intermediary cannot be activated yet.")
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["missing"] = string.Join(",", missing) },
            };
        }

        row.Status = Codes.Of(to.Value);
        row.RecordVersion++;
        row.UpdatedAt = clock.Now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.PTY, "STALE", "The intermediary changed meanwhile; reload it.");
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(IntermediaryStatusChangedV1.Descriptor),
            "Intermediary",
            row.IntermediaryId.Value.ToString(),
            new IntermediaryStatusChangedV1 { IntermediaryId = row.IntermediaryId, OldStatus = Codes.Of(from), NewStatus = row.Status },
            BusinessKeys.Empty.With("intermediaryId", row.IntermediaryId.Value.ToString())));

        return new IntermediaryUpdateResponse { Intermediary = (await queries.ViewAsync(row.IntermediaryId, cancellationToken).ConfigureAwait(false))! };
    }
}

/// <summary>Audit facts of the intermediary commands.</summary>
internal sealed class IntermediaryAuditor : ICommandAuditor<CreateIntermediary, IntermediaryCreateResponse>, ICommandAuditor<UpdateIntermediary, IntermediaryUpdateResponse>
{
    public CommandAuditFacts Describe(CreateIntermediary command, Result<IntermediaryCreateResponse>? result) =>
        result is { IsSuccess: true } ok ? Facts(ok.Value.Intermediary, null) : new CommandAuditFacts();

    public CommandAuditFacts Describe(UpdateIntermediary command, Result<IntermediaryUpdateResponse>? result) =>
        result is { IsSuccess: true } ok
            ? Facts(ok.Value.Intermediary, "ONBOARDING")
            : new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.PTY, "Intermediary", command.IntermediaryId) };

    private static CommandAuditFacts Facts(IntermediaryView view, string? before) => new()
    {
        ObjectRef = ObjectRef.For(ModuleCode.PTY, "Intermediary", view.IntermediaryId),
        ObjectNumber = view.ProducerCode,
        BusinessKeys = BusinessKeys.Empty.With("intermediaryId", view.IntermediaryId.Value.ToString()),
        Changes = AuditDiff.Compute(before is null ? null : new { status = before }, new { status = view.Status.ToString(), producerCode = view.ProducerCode }),
    };
}
