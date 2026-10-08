using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary><c>clm.PayeeAccount.capture</c> as a command of the platform pipeline.</summary>
internal sealed record CapturePayeeAccount(PayeeAccountCaptureRequest Request) : ICommand<PayeeAccountCaptureResponse>;

/// <summary>Shape rules (the IBAN itself is BIL's to check: BIL-ERR-IBAN-INVALID). No value is echoed in messages.</summary>
internal sealed class CapturePayeeAccountValidator : AbstractValidator<CapturePayeeAccount>
{
    public CapturePayeeAccountValidator()
    {
        RuleFor(c => c.Request.Iban).NotEmpty().MaximumLength(42).WithMessage("An IBAN is required.");
        RuleFor(c => c.Request.HolderName).NotEmpty().MaximumLength(140).WithMessage("A holder name is required.");
        RuleFor(c => c.Request.EvidenceRef).MaximumLength(200);
    }
}

/// <summary>
/// Captures a payee's bank account in a claim's context (REQ-CLM-123, -131): the payee must be the claim's insured or one of
/// its claimants (CLM-ERR-PAYEE-NOT-ON-CLAIM); the account is registered with BIL in process (<c>bil.PayeeAccount.create</c>,
/// purpose CLAIM_PAYMENT, source STAFF; BIL encrypts the IBAN, runs VoP and sets cooling-off) and CLM keeps only BIL's
/// account id, the masked IBAN (last four), the verification status and the cooling-off date (PayeeAccountView, R-38).
/// The IBAN and holder name are never stored, logged, audited or published by CLM; the result holds no P2 value.
/// </summary>
internal sealed class CapturePayeeAccountHandler(
    ClaimsDbContext db,
    RequestContext context,
    IClock clock,
    ClaimProtection protection,
    IBillingPayeeAccountService payeeAccounts) : ICommandHandler<CapturePayeeAccount, PayeeAccountCaptureResponse>
{
    public async Task<Result<PayeeAccountCaptureResponse>> HandleAsync(CapturePayeeAccount command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = protection.Current(context);
        var claim = await ClaimSupport.LoadAsync(db, legalEntity, request.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return ClaimSupport.NotFound("claim");
        }

        if (claim.InsuredPartyId != request.PartyId
            && !await db.Claimants.AnyAsync(c => c.ClaimId == claim.ClaimId && c.PartyId == request.PartyId, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.CLM, "PAYEE-NOT-ON-CLAIM", "The payee is neither the insured nor a claimant of this claim.");
        }

        PayeeAccountCreateResponse created;
        try
        {
            created = await payeeAccounts.CreateAsync(
                new PayeeAccountCreateRequest
                {
                    PartyId = request.PartyId,
                    Purpose = PayeeAccountCreateRequest.PurposeValue.ClaimPayment,
                    Iban = request.Iban,
                    HolderName = request.HolderName,
                    Source = PayeeAccountCreateRequest.SourceValue.Staff,
                    EvidenceRef = request.EvidenceRef,
                },
                new CommandOptions(context.IdempotencyKey ?? IdempotencyKey.New()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.BIL)
        {
            return ex.Error;
        }

        var now = clock.Now;
        var row = await db.PayeeAccounts.SingleOrDefaultAsync(a => a.ClaimId == claim.ClaimId && a.PayeeAccountId == created.PayeeAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new PayeeAccountViewRow
            {
                PayeeAccountId = created.PayeeAccountId, ClaimId = claim.ClaimId, PartyId = request.PartyId, LegalEntityId = legalEntity,
                Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = context.Actor.ToString(),
            };
            db.PayeeAccounts.Add(row);
        }
        else
        {
            row.RecordVersion++;
        }

        row.MaskedIban = created.MaskedIban;
        row.VerificationStatus = Domain.Codes.Of(created.VerificationStatus);
        row.CoolingOffUntil = created.CoolingOffUntil;
        row.IsChange = created.Change;
        row.UpdatedAt = now;
        return new PayeeAccountCaptureResponse { PayeeAccount = FinancialsReader.PayeeAccount(row) };
    }
}

/// <summary>Audit facts: the BIL account id, masked IBAN and verification only (never the IBAN or the holder name).</summary>
internal sealed class CapturePayeeAccountAuditor : ICommandAuditor<CapturePayeeAccount, PayeeAccountCaptureResponse>
{
    public CommandAuditFacts Describe(CapturePayeeAccount command, Result<PayeeAccountCaptureResponse>? result)
    {
        var keys = BusinessKeys.Empty.With("claimId", command.Request.ClaimId.Value.ToString());
        if (result is not { IsSuccess: true } ok)
        {
            return new CommandAuditFacts { ObjectRef = ObjectRef.For(ModuleCode.CLM, "Claim", command.Request.ClaimId), BusinessKeys = keys };
        }

        var account = ok.Value.PayeeAccount;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "PayeeAccount", account.PayeeAccountId.ToString("D")),
            BusinessKeys = keys.With("payeeAccountId", account.PayeeAccountId.ToString()),
            Changes = AuditDiff.Compute(null, new { maskedIban = account.MaskedIban, verificationStatus = account.VerificationStatus, change = account.Change }),
        };
    }
}
