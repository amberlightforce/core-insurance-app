using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.PayeeAccount.create</c>: registers (or changes) a payee bank account (REQ-BIL-343).</summary>
internal sealed record CreatePayeeAccount(PayeeAccountCreateRequest Request) : ICommand<PayeeAccountCreateResponse>;

/// <summary>Shape rules. The IBAN itself is checked in the handler so its value never appears in a field error.</summary>
internal sealed class CreatePayeeAccountValidator : AbstractValidator<CreatePayeeAccount>
{
    public CreatePayeeAccountValidator()
    {
        RuleFor(c => c.Request.Iban).NotEmpty().WithErrorCode("IBAN_REQUIRED").WithMessage("The IBAN is required.");
        RuleFor(c => c.Request.HolderName).NotEmpty().MaximumLength(140);
        RuleFor(c => c.Request.EvidenceRef).MaximumLength(200);
        RuleFor(c => c.Request.Purpose).IsInEnum();
        RuleFor(c => c.Request.Source).IsInEnum().When(c => c.Request.Source is not null);
    }
}

/// <summary>
/// Validates the IBAN (ISO 13616 mod-97, BIL-ERR-IBAN-INVALID), stores it field-encrypted with a blind index and its last
/// four characters for display (REQ-BIL-103, REQ-BIL-334), runs verification of payee through the bound adapter (the slice's
/// stub answers Match; none bound = VoPNotAvailable, REQ-BIL-203/214) and sets <c>cooling_off_until</c> (REQ-BIL-199,
/// BR-BIL-062): registration date + cooling-off days for a changed account, the registration date for a first one. The same IBAN registered again for the party and purpose returns the existing account; a different IBAN
/// supersedes the Active one (valid_to = today, never an overwrite) and is marked as a change, which the disbursement
/// service holds within cooling-off. The response, the audit record and the logs carry the masked IBAN only.
/// </summary>
internal sealed partial class CreatePayeeAccountHandler(
    BillingDbContext db,
    RequestContext context,
    IClock clock,
    PayeeProtection protection,
    IServiceProvider services,
    IOptions<BillingOptions> options,
    ILogger<CreatePayeeAccountHandler> logger) : ICommandHandler<CreatePayeeAccount, PayeeAccountCreateResponse>
{
    public async Task<Result<PayeeAccountCreateResponse>> HandleAsync(CreatePayeeAccount command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!Iban.TryParse(request.Iban, out var iban))
        {
            return DomainError.Of(ModuleCode.BIL, "IBAN-INVALID", "The IBAN is not valid (ISO 13616 structure or check digits).");
        }

        var holderName = request.HolderName.Trim();
        if (holderName.Length == 0 || holderName.Any(char.IsControl))
        {
            return DomainError.Of(ModuleCode.BIL, "CHARSET", "The holder name contains characters a bank transfer cannot carry.");
        }

        var legalEntity = protection.Current(context);
        var purpose = Codes.Of(request.Purpose);
        var active = Codes.Of(PayeeAccountStatus.Active);
        var current = await db.PayeeAccounts
            .SingleOrDefaultAsync(a => a.LegalEntityId == legalEntity && a.PartyId == request.PartyId && a.Purpose == purpose && a.Status == active, cancellationToken)
            .ConfigureAwait(false);
        if (current is not null)
        {
            var candidates = await protection.IndexCandidatesAsync(legalEntity, iban, cancellationToken).ConfigureAwait(false);
            if (candidates.Contains(current.IbanBlindIndex, StringComparer.Ordinal))
            {
                return Respond(current);
            }
        }

        var now = clock.Now;
        var today = now.ToBusinessDate(options.Value.Zone);
        var id = Guid.CreateVersion7();
        var vop = await VerifyAsync(iban.Value, holderName, cancellationToken).ConfigureAwait(false);

        if (current is not null)
        {
            // A change supersedes the Active account with a new valid period (REQ-BIL-343): saved first, so the unique
            // "one Active account per party and purpose" index sees the old row closed before the new one arrives.
            current.Status = Codes.Of(PayeeAccountStatus.Superseded);
            current.ValidTo = today;
            current.RecordVersion++;
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Stale();
            }
        }

        var row = new PayeeAccountRow
        {
            PayeeAccountId = id,
            LegalEntityId = legalEntity,
            PartyId = request.PartyId,
            Purpose = purpose,
            IbanEncrypted = await protection.EncryptAsync(legalEntity, id, iban, cancellationToken).ConfigureAwait(false),
            IbanBlindIndex = await protection.IndexAsync(legalEntity, iban, cancellationToken).ConfigureAwait(false),
            IbanLast4 = IbanMask.Last4(iban),
            HolderName = holderName,
            Source = request.Source is { } source ? Codes.Of(source) : "STAFF",
            EvidenceRef = request.EvidenceRef,
            VerificationStatus = Codes.Of(PayeeAccountMapping.StatusOf(vop.Outcome)),
            VopResult = Codes.Of(vop.Outcome),
            VopSuggestedName = vop.SuggestedName,
            VopCheckedAt = now,
            ValidFrom = today,
            // Cooling-off applies only to a changed account (REQ-BIL-199); a first account reports no window it is not held by.
            CoolingOffUntil = current is null ? today : today.AddDays(options.Value.PayeeCoolingOffDays),
            IsChange = current is not null,
            SupersedesId = current?.PayeeAccountId,
            Status = active,
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
        db.PayeeAccounts.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_payee_account_active" })
        {
            return Stale();
        }

        var response = Respond(row);
        LogRegistered(logger, id, request.PartyId.Value, purpose, response.MaskedIban, row.VopResult, row.IsChange);
        return response;
    }

    private async Task<VopCheck> VerifyAsync(string iban, string holderName, CancellationToken cancellationToken)
    {
        var verifier = services.GetService<IPayeeVerifier>();
        if (verifier is null)
        {
            return new VopCheck(VopOutcome.NotAvailable, null);
        }

        try
        {
            return await verifier.VerifyAsync(iban, holderName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // REQ-BIL-214: VoP down is NotAvailable, never an error that blocks registration; no IBAN in the log.
            LogVopUnavailable(logger, ex.GetType().Name);
            return new VopCheck(VopOutcome.NotAvailable, null);
        }
    }

    private static PayeeAccountCreateResponse Respond(PayeeAccountRow row) => new()
    {
        PayeeAccountId = row.PayeeAccountId,
        VerificationStatus = PayeeAccountMapping.Api(Codes.Parse<PayeeVerification>(row.VerificationStatus)),
        CoolingOffUntil = row.CoolingOffUntil,
        MaskedIban = IbanMask.Mask(row.IbanLast4),
        Change = row.IsChange,
    };

    private static DomainError Stale() =>
        DomainError.Of(ModuleCode.BIL, "STALE", "Another account was registered for this party and purpose meanwhile; load it and try again.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Payee account {PayeeAccountId} registered for party {PartyId}, purpose {Purpose}, IBAN {MaskedIban}, VoP {VopResult}, change {Change}.")]
    private static partial void LogRegistered(ILogger logger, Guid payeeAccountId, Guid partyId, string purpose, string maskedIban, string? vopResult, bool change);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Verification of payee unavailable ({ExceptionType}); the account is stored as VoPNotAvailable.")]
    private static partial void LogVopUnavailable(ILogger logger, string exceptionType);
}

/// <summary>Audit facts: the account id and its masked IBAN; never the IBAN or the holder name.</summary>
internal sealed class CreatePayeeAccountAuditor : ICommandAuditor<CreatePayeeAccount, PayeeAccountCreateResponse>
{
    public CommandAuditFacts Describe(CreatePayeeAccount command, Result<PayeeAccountCreateResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("partyId", command.Request.PartyId.Value.ToString());
        return result is { IsSuccess: true } ok
            ? new CommandAuditFacts
            {
                ObjectRef = new ObjectRef(ModuleCode.BIL, "PayeeAccount", ok.Value.PayeeAccountId.ToString("D")),
                BusinessKeys = keys.With("payeeAccountId", ok.Value.PayeeAccountId.ToString()),
                Changes = AuditDiff.Compute(null, new
                {
                    purpose = Codes.Of(command.Request.Purpose),
                    maskedIban = ok.Value.MaskedIban,
                    verificationStatus = ok.Value.VerificationStatus.ToString(),
                    coolingOffUntil = ok.Value.CoolingOffUntil.ToString(),
                    change = ok.Value.Change,
                }),
            }
            : new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.PTY, "Party", command.Request.PartyId), BusinessKeys = keys };
    }
}

/// <summary>Mapping between the stored codes and the contract enums of payee accounts.</summary>
internal static class PayeeAccountMapping
{
    public static PayeeVerification StatusOf(VopOutcome outcome) => outcome switch
    {
        VopOutcome.Match => PayeeVerification.VopMatched,
        VopOutcome.CloseMatch => PayeeVerification.VopCloseMatch,
        VopOutcome.NoMatch => PayeeVerification.VopNoMatch,
        _ => PayeeVerification.VopNotAvailable,
    };

    public static PayeeVerificationStatus Api(PayeeVerification status) => status switch
    {
        PayeeVerification.Unverified => PayeeVerificationStatus.Unverified,
        PayeeVerification.VopMatched => PayeeVerificationStatus.VoPMatched,
        PayeeVerification.VopCloseMatch => PayeeVerificationStatus.VoPCloseMatch,
        PayeeVerification.VopNoMatch => PayeeVerificationStatus.VoPNoMatch,
        PayeeVerification.VopNotAvailable => PayeeVerificationStatus.VoPNotAvailable,
        _ => PayeeVerificationStatus.Confirmed,
    };
}
