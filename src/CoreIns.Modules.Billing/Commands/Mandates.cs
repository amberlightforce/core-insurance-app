using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.PaymentInstrument.add</c>: registers a payer's bank account (IBAN P2, field-encrypted) with its verification of payee (BO-BIL-06a).</summary>
internal sealed record AddPaymentInstrument(PaymentInstrumentAddRequest Request) : ICommand<PaymentInstrumentAddResponse>;

/// <summary><c>bil.PaymentInstrument.remove</c>: removes an instrument no live mandate uses (BO-BIL-06a).</summary>
internal sealed record RemovePaymentInstrument(PaymentInstrumentRemoveRequest Request) : ICommand<PaymentInstrumentRemoveResponse>;

/// <summary><c>bil.Mandate.create</c>: a DRAFT SEPA mandate on a billing account's instrument (BO-BIL-06a).</summary>
internal sealed record CreateMandate(MandateCreateRequest Request) : ICommand<MandateCreateResponse>;

/// <summary><c>bil.Mandate.sign</c>: DRAFT to ACTIVE with a consent evidence id (BO-BIL-06a).</summary>
internal sealed record SignMandate(MandateSignRequest Request) : ICommand<MandateSignResponse>;

/// <summary><c>bil.Mandate.amend</c>: moves an ACTIVE mandate to another instrument; the sequence type returns to FRST (BO-BIL-06a).</summary>
internal sealed record AmendMandate(MandateAmendRequest Request) : ICommand<MandateAmendResponse>;

/// <summary><c>bil.Mandate.cancel</c>: DRAFT or ACTIVE to CANCELLED, final (BO-BIL-06a).</summary>
internal sealed record CancelMandate(MandateCancelRequest Request) : ICommand<MandateCancelResponse>;

internal sealed class AddPaymentInstrumentValidator : AbstractValidator<AddPaymentInstrument>
{
    public AddPaymentInstrumentValidator()
    {
        // The IBAN itself is checked in the handler so its value never appears in a field error.
        RuleFor(c => c.Request.Holder).NotEmpty().MaximumLength(140);
        RuleFor(c => c.Request.Iban).NotEmpty().When(c => c.Request.OrCardToken is null).WithErrorCode("IBAN_REQUIRED").WithMessage("The IBAN is required.");
    }
}

internal sealed class RemovePaymentInstrumentValidator : AbstractValidator<RemovePaymentInstrument>
{
    public RemovePaymentInstrumentValidator() => RuleFor(c => c.Request.InstrumentId).NotEmpty();
}

internal sealed class CreateMandateValidator : AbstractValidator<CreateMandate>
{
    public CreateMandateValidator()
    {
        RuleFor(c => c.Request.InstrumentId).NotEmpty();
        RuleFor(c => c.Request.BillingAccountId.Value).NotEmpty();
    }
}

internal sealed class SignMandateValidator : AbstractValidator<SignMandate>
{
    public SignMandateValidator()
    {
        RuleFor(c => c.Request.MandateId).NotEmpty();
        RuleFor(c => c.Request.SignatureEvidenceId).NotEmpty().MaximumLength(100);
    }
}

internal sealed class AmendMandateValidator : AbstractValidator<AmendMandate>
{
    public AmendMandateValidator()
    {
        RuleFor(c => c.Request.MandateId).NotEmpty();
        RuleFor(c => c.Request.InstrumentId).NotEmpty();
        RuleFor(c => c.Request.SignatureEvidenceId).NotEmpty().MaximumLength(100);
    }
}

internal sealed class CancelMandateValidator : AbstractValidator<CancelMandate>
{
    public CancelMandateValidator()
    {
        RuleFor(c => c.Request.MandateId).NotEmpty();
        RuleFor(c => c.Request.Reason).MaximumLength(200);
    }
}

/// <summary>Shared lookups, mappings and the state machine of mandates and instruments (BO-BIL-06a).</summary>
internal static class MandateRules
{
    public static DomainError State(string message) => DomainError.Of(ModuleCode.BIL, "MANDATE-STATE", message);

    public static DomainError Stale() =>
        DomainError.Of(ModuleCode.BIL, "STALE", "The mandate or instrument changed meanwhile; load it and try again.");

    public static LegalEntityId Current(RequestContext context, ILegalEntityDirectory legalEntities) =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));

    /// <summary>Mandate states in which it can still collect or be signed.</summary>
    public static readonly string[] Live = [Codes.Of(MandateStatus.Draft), Codes.Of(MandateStatus.Active)];

    /// <summary>The unique mandate reference: at most 35 characters, unique per legal entity (also enforced by an index).</summary>
    public static string NewReference(BusinessDate today) =>
        $"CI{today.Value:yyMMdd}{Guid.NewGuid():N}"[..28].ToUpperInvariant();

    /// <summary>The instrument when it exists, is active and belongs to the payer; otherwise the refusal.</summary>
    public static async Task<Result<PaymentInstrumentRow>> UsableInstrumentAsync(
        BillingDbContext db, LegalEntityId legalEntity, Guid instrumentId, PartyId payer, CancellationToken cancellationToken)
    {
        var instrument = await db.PaymentInstruments.SingleOrDefaultAsync(i => i.InstrumentId == instrumentId && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (instrument is null)
        {
            return BillingErrors.NotFound("instrument");
        }

        if (instrument.Status != Codes.Of(InstrumentStatus.Active))
        {
            return State("The instrument has been removed.");
        }

        return instrument.PartyId == payer
            ? instrument
            : DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", "The instrument belongs to another party than the account's payer.");
    }

    public static async Task<Result<MandateRow>> LoadAsync(BillingDbContext db, LegalEntityId legalEntity, Guid mandateId, CancellationToken cancellationToken)
    {
        var row = await db.Mandates.SingleOrDefaultAsync(m => m.MandateId == mandateId && m.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        return row is null ? BillingErrors.NotFound("mandate") : row;
    }

    /// <summary>Saves a changed row, turning a lost update into <c>BIL-ERR-STALE</c> and a second live mandate into the state refusal.</summary>
    public static async Task<DomainError?> SaveAsync(BillingDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_mandate_live_account" })
        {
            return State("The billing account already has a live mandate; cancel it first.");
        }
    }
}

/// <summary>
/// Validates the IBAN (ISO 13616, BIL-ERR-IBAN-INVALID), stores it field-encrypted under the legal entity's key with a blind index and its
/// last four characters, and records the verification of payee (none bound is NOT_AVAILABLE: the instrument is kept, REQ-BIL-214). The same
/// IBAN added again for the party returns the existing instrument. The response, the audit record and the logs carry the masked IBAN only.
/// </summary>
internal sealed class AddPaymentInstrumentHandler(
    BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, PayeeProtection protection, IServiceProvider services)
    : ICommandHandler<AddPaymentInstrument, PaymentInstrumentAddResponse>
{
    public async Task<Result<PaymentInstrumentAddResponse>> HandleAsync(AddPaymentInstrument command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request.OrCardToken is not null)
        {
            // TODO(hardening): card tokens (PSP vault reference) as instruments; only bank accounts are supported.
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", "Card tokens are not supported as instruments yet.");
        }

        if (!Iban.TryParse(request.Iban, out var iban))
        {
            return DomainError.Of(ModuleCode.BIL, "IBAN-INVALID", "The IBAN is not valid (ISO 13616 structure or check digits).");
        }

        var holder = request.Holder.Trim();
        if (holder.Length == 0 || holder.Any(char.IsControl))
        {
            return DomainError.Of(ModuleCode.BIL, "CHARSET", "The holder name contains characters a bank transfer cannot carry.");
        }

        var legalEntity = MandateRules.Current(context, legalEntities);
        var active = Codes.Of(InstrumentStatus.Active);
        var candidates = await protection.IndexCandidatesAsync(legalEntity, iban, cancellationToken).ConfigureAwait(false);
        var existing = await db.PaymentInstruments.AsNoTracking()
            .Where(i => i.LegalEntityId == legalEntity && i.PartyId == request.PartyId && i.Status == active && candidates.Contains(i.IbanBlindIndex))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Respond(existing);
        }

        var vop = await VerifyAsync(iban.Value, holder, cancellationToken).ConfigureAwait(false);
        var id = Guid.CreateVersion7();
        var row = new PaymentInstrumentRow
        {
            InstrumentId = id,
            LegalEntityId = legalEntity,
            PartyId = request.PartyId,
            IbanEncrypted = await protection.EncryptInstrumentAsync(legalEntity, id, iban, cancellationToken).ConfigureAwait(false),
            IbanBlindIndex = await protection.IndexAsync(legalEntity, iban, cancellationToken).ConfigureAwait(false),
            IbanLast4 = IbanMask.Last4(iban),
            HolderName = holder,
            VopResult = Codes.Of(vop),
            Status = active,
            CreatedAt = clock.Now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.PaymentInstruments.Add(row);
        var failure = await MandateRules.SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return failure is null ? Respond(row) : failure;
    }

    private async Task<VopOutcome> VerifyAsync(string iban, string holder, CancellationToken cancellationToken)
    {
        var verifier = services.GetService<IPayeeVerifier>();
        if (verifier is null)
        {
            return VopOutcome.NotAvailable;
        }

        try
        {
            return (await verifier.VerifyAsync(iban, holder, cancellationToken).ConfigureAwait(false)).Outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return VopOutcome.NotAvailable;
        }
    }

    private static PaymentInstrumentAddResponse Respond(PaymentInstrumentRow row) => new()
    {
        Instrument = MandateReader.View(row),
        VopResult = MandateReader.Vop<PaymentInstrumentAddResponse.VopResultValue>(row.VopResult),
    };
}

internal sealed class RemovePaymentInstrumentHandler(BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
    : ICommandHandler<RemovePaymentInstrument, PaymentInstrumentRemoveResponse>
{
    public async Task<Result<PaymentInstrumentRemoveResponse>> HandleAsync(RemovePaymentInstrument command, CancellationToken cancellationToken)
    {
        var legalEntity = MandateRules.Current(context, legalEntities);
        var id = command.Request.InstrumentId;
        var row = await db.PaymentInstruments.SingleOrDefaultAsync(i => i.InstrumentId == id && i.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return BillingErrors.NotFound("instrument");
        }

        if (row.Status == Codes.Of(InstrumentStatus.Active))
        {
            if (await db.Mandates.AnyAsync(m => m.InstrumentId == id && MandateRules.Live.Contains(m.Status), cancellationToken).ConfigureAwait(false))
            {
                return MandateRules.State("A draft or active mandate uses the instrument; cancel or amend the mandate first.");
            }

            row.Status = Codes.Of(InstrumentStatus.Removed);
            row.RemovedAt = clock.Now;
            row.RecordVersion++;
            if (await MandateRules.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                return failure;
            }
        }

        return new PaymentInstrumentRemoveResponse
        {
            Instrument = MandateReader.View(row),
            VopResult = MandateReader.Vop<PaymentInstrumentRemoveResponse.VopResultValue>(row.VopResult),
        };
    }
}

internal sealed class CreateMandateHandler(
    BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, MandateReader reader, IOptions<BillingOptions> options)
    : ICommandHandler<CreateMandate, MandateCreateResponse>
{
    public async Task<Result<MandateCreateResponse>> HandleAsync(CreateMandate command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = MandateRules.Current(context, legalEntities);
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.BillingAccountId == request.BillingAccountId && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return BillingErrors.NotFound("billing account");
        }

        if (account.Status == Codes.Of(BillingAccountStatus.Closed))
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", "The billing account is closed.");
        }

        var instrument = await MandateRules.UsableInstrumentAsync(db, legalEntity, request.InstrumentId, account.PayerPartyId, cancellationToken).ConfigureAwait(false);
        if (instrument.IsFailure)
        {
            return instrument.Error!;
        }

        var now = clock.Now;
        var row = new MandateRow
        {
            MandateId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            BillingAccountId = account.BillingAccountId,
            PayerPartyId = account.PayerPartyId,
            InstrumentId = request.InstrumentId,
            MandateReference = MandateRules.NewReference(now.ToBusinessDate(options.Value.Zone)),
            SequenceType = Codes.Of(MandateSequence.Frst),
            Status = Codes.Of(MandateStatus.Draft),
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.Mandates.Add(row);
        if (await MandateRules.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        return new MandateCreateResponse { Mandate = await reader.ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class SignMandateHandler(
    BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, MandateReader reader, IOptions<BillingOptions> options)
    : ICommandHandler<SignMandate, MandateSignResponse>
{
    public async Task<Result<MandateSignResponse>> HandleAsync(SignMandate command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = MandateRules.Current(context, legalEntities);
        var loaded = await MandateRules.LoadAsync(db, legalEntity, request.MandateId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var row = loaded.Value;
        if (row.Status != Codes.Of(MandateStatus.Draft))
        {
            return MandateRules.State($"Only a draft mandate can be signed; this one is {row.Status}.");
        }

        var evidence = request.SignatureEvidenceId.Trim();
        if (evidence.Length == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "A consent evidence id is required to sign a mandate.");
        }

        var today = clock.Now.ToBusinessDate(options.Value.Zone);
        var signed = request.SignatureDate ?? today;
        if (signed > today)
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "The signature date is in the future.");
        }

        var usable = await MandateRules.UsableInstrumentAsync(db, legalEntity, row.InstrumentId!.Value, row.PayerPartyId, cancellationToken).ConfigureAwait(false);
        if (usable.IsFailure)
        {
            return usable.Error!;
        }

        row.Status = Codes.Of(MandateStatus.Active);
        row.SequenceType = Codes.Of(MandateSequence.Frst);
        row.SignatureDate = signed;
        row.SignatureEvidenceId = evidence;
        row.RecordVersion++;
        if (await MandateRules.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        return new MandateSignResponse { Mandate = await reader.ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class AmendMandateHandler(
    BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, MandateReader reader, IOptions<BillingOptions> options)
    : ICommandHandler<AmendMandate, MandateAmendResponse>
{
    public async Task<Result<MandateAmendResponse>> HandleAsync(AmendMandate command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = MandateRules.Current(context, legalEntities);
        var loaded = await MandateRules.LoadAsync(db, legalEntity, request.MandateId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var row = loaded.Value;
        if (row.Status != Codes.Of(MandateStatus.Active))
        {
            return MandateRules.State($"Only an active mandate can be amended; this one is {row.Status}.");
        }

        if (request.InstrumentId == row.InstrumentId)
        {
            return DomainError.Of(ModuleCode.BIL, "NOT-ELIGIBLE", "The mandate already uses this instrument; there is nothing to amend.");
        }

        var evidence = request.SignatureEvidenceId.Trim();
        if (evidence.Length == 0)
        {
            return DomainError.Of(ModuleCode.BIL, "VALIDATION", "A consent evidence id is required to amend a mandate.");
        }

        var usable = await MandateRules.UsableInstrumentAsync(db, legalEntity, request.InstrumentId, row.PayerPartyId, cancellationToken).ConfigureAwait(false);
        if (usable.IsFailure)
        {
            return usable.Error!;
        }

        // A changed debtor account starts a new first collection (SEPA: amendment indicator, sequence FRST again); fresh consent evidence is required.
        row.InstrumentId = request.InstrumentId;
        row.IbanMasked = null;
        row.SequenceType = Codes.Of(MandateSequence.Frst);
        row.AmendmentCount++;
        row.SignatureDate = clock.Now.ToBusinessDate(options.Value.Zone);
        row.SignatureEvidenceId = evidence;
        row.RecordVersion++;
        if (await MandateRules.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        return new MandateAmendResponse { Mandate = await reader.ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class CancelMandateHandler(
    BillingDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, MandateReader reader)
    : ICommandHandler<CancelMandate, MandateCancelResponse>
{
    public async Task<Result<MandateCancelResponse>> HandleAsync(CancelMandate command, CancellationToken cancellationToken)
    {
        var legalEntity = MandateRules.Current(context, legalEntities);
        var loaded = await MandateRules.LoadAsync(db, legalEntity, command.Request.MandateId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var row = loaded.Value;
        if (!MandateRules.Live.Contains(row.Status))
        {
            return MandateRules.State("The mandate is already cancelled.");
        }

        // TODO(hardening): a migrated mandate is cancelled here only; its migration_mandate row (app role: no UPDATE) keeps status ACTIVE.
        row.Status = Codes.Of(MandateStatus.Cancelled);
        row.CancelledAt = clock.Now;
        row.CancelReason = command.Request.Reason;
        row.RecordVersion++;
        if (await MandateRules.SaveAsync(db, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        return new MandateCancelResponse { Mandate = await reader.ViewAsync(row, cancellationToken).ConfigureAwait(false) };
    }
}

/// <summary>Audit facts of the mandate and instrument commands: ids, states and masked values; never an IBAN or a holder name.</summary>
internal sealed class MandateAuditor<TCommand, TResponse> : ICommandAuditor<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public CommandAuditFacts Describe(TCommand command, Result<TResponse>? result)
    {
        var (type, id, requested) = command switch
        {
            AddPaymentInstrument => ("PaymentInstrument", Guid.Empty, command.GetType().Name),
            RemovePaymentInstrument c => ("PaymentInstrument", c.Request.InstrumentId, command.GetType().Name),
            CreateMandate => ("Mandate", Guid.Empty, command.GetType().Name),
            SignMandate c => ("Mandate", c.Request.MandateId, command.GetType().Name),
            AmendMandate c => ("Mandate", c.Request.MandateId, command.GetType().Name),
            CancelMandate c => ("Mandate", c.Request.MandateId, command.GetType().Name),
            _ => ("Mandate", Guid.Empty, command.GetType().Name),
        };
        object? status = null;
        if (result is { IsSuccess: true } ok)
        {
            switch (ok.Value)
            {
                case PaymentInstrumentAddResponse r:
                    id = r.Instrument.InstrumentId;
                    status = new { r.Instrument.MaskedIban, status = r.Instrument.Status.ToString() };
                    break;
                case PaymentInstrumentRemoveResponse r:
                    status = new { r.Instrument.MaskedIban, status = r.Instrument.Status.ToString() };
                    break;
                case MandateCreateResponse r:
                    (id, status) = (r.Mandate.MandateId, Facts(r.Mandate));
                    break;
                case MandateSignResponse r:
                    status = Facts(r.Mandate);
                    break;
                case MandateAmendResponse r:
                    status = Facts(r.Mandate);
                    break;
                case MandateCancelResponse r:
                    status = Facts(r.Mandate);
                    break;
            }
        }

        var key = id == Guid.Empty ? "pending" : id.ToString("D");
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, type, key),
            BusinessKeys = BusinessKeys.Empty.With(type == "Mandate" ? "mandateId" : "instrumentId", key),
            Changes = status is null ? [] : AuditDiff.Compute(null, new { operation = requested, state = status }),
        };
    }

    private static object Facts(MandateView m) => new
    {
        m.MandateReference,
        status = m.Status.ToString(),
        sequenceType = m.SequenceType.ToString(),
        m.AmendmentCount,
        m.SignatureEvidenceId,
        m.MaskedIban,
    };
}
