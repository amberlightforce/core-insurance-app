using System.Text.RegularExpressions;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Billing.Commands;

/// <summary><c>bil.TaxLevy.requestLevyPayment</c> (BO-BIL-14): the payable of one levy authority and month, and its payment request.</summary>
internal sealed record RequestLevyPayment(TaxLevyRequestLevyPaymentRequest Request) : ICommand<TaxLevyRequestLevyPaymentResponse>;

internal sealed partial class RequestLevyPaymentValidator : AbstractValidator<RequestLevyPayment>
{
    public RequestLevyPaymentValidator()
    {
        RuleFor(c => c.Request.LevyType).NotEmpty().Must(t => LevyTypePattern().IsMatch(t ?? string.Empty)).WithErrorCode("LEVY_TYPE")
            .WithMessage("The levy type is a code of capital letters, digits and underscores.");
        RuleFor(c => c.Request.Period).NotEmpty().Must(p => TaxLevyReader.IsPeriod(p ?? string.Empty)).WithErrorCode("PERIOD").WithMessage("period must be yyyy-MM.");
        RuleFor(c => c.Request.StatementReference).MaximumLength(140);
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex LevyTypePattern();
}

/// <summary>
/// Creates the payable of one levy authority and month from the levy lines of the sub-ledger (LA-07, per charge type) and requests its
/// payment as a <c>BIL_LEVY</c> disbursement (SEPA_CT, EUR) through the shared disbursement service: the requester needs BIL.DISBURSEMENT
/// authority, the payee account must be Active, purpose LEVY and verified, and a second person approves the release
/// (<c>bil.Disbursement.approveRelease</c>). The amount is the net levy of the month, never free text. One payable per (levy authority,
/// month): a repeat is BIL-ERR-DUPLICATE with the existing ids; when its payment was stopped, rejected, voided or returned, a new
/// request re-uses the payable. Levy that is not Settled (PendingOpinion rows, unverified lines) is refused in Production (D-USR-19).
/// TODO(hardening): levy collected after a payable was created (late credits or invoices) needs a supplementary payable; today it shows as outstanding.
/// TODO(hardening): the levy authority is the levy charge type; a registry of authorities with their payee party and account belongs in the country pack.
/// </summary>
internal sealed class RequestLevyPaymentHandler(
    BillingDbContext db, TaxLevyReader reader, LedgerWriter ledger, RequestContext context, IClock clock, IHostEnvironment environment, IServiceProvider services)
    : ICommandHandler<RequestLevyPayment, TaxLevyRequestLevyPaymentResponse>
{
    public async Task<Result<TaxLevyRequestLevyPaymentResponse>> HandleAsync(RequestLevyPayment command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var legalEntity = ledger.LegalEntityId;
        var currency = Currency.EUR;

        var basis = await reader.LevyBasisAsync(legalEntity, request.LevyType, request.Period, currency.Code, cancellationToken).ConfigureAwait(false);
        if (basis is null || basis.Net <= 0m)
        {
            return DomainError.Of(ModuleCode.BIL, "NOTHING-PAYABLE", $"There is no positive net {currency.Code} levy of type {request.LevyType} in {request.Period} to pay.");
        }

        var amount = new Money(basis.Net, currency);
        if (!amount.IsRoundedToMinorUnits)
        {
            return DomainError.Of(ModuleCode.BIL, "AMOUNT-MISMATCH", "The levy of the month is not a whole number of cents; the ledger lines must be corrected before it is paid.");
        }

        // D-USR-19: a levy whose legal status is not settled (PendingOpinion row, unverified line) is never paid in Production.
        if (environment.IsProduction() && basis.LegalStatus != LevyLegal.Settled)
        {
            return DomainError.Of(ModuleCode.BIL, "LEVY-UNSETTLED", $"The levy {request.LevyType} of {request.Period} rests on rows that are {basis.LegalStatus}, not Settled; it is not paid in Production.");
        }

        var payable = await db.LevyPayables.SingleOrDefaultAsync(
            p => p.LegalEntityId == legalEntity && p.LevyAuthority == request.LevyType && p.Period == request.Period, cancellationToken).ConfigureAwait(false);
        if (payable is not null)
        {
            var state = await db.Disbursements.AsNoTracking().Where(d => d.DisbursementId == payable.DisbursementId).Select(d => d.State).SingleAsync(cancellationToken).ConfigureAwait(false);
            if (LevyPaymentStates.Of(state) != LevyPaymentState.Dead)
            {
                return Duplicate(payable);
            }
        }

        var sourceId = request.LevyType + ":" + request.Period;
        DisbursementRequestResponse disbursement;
        try
        {
            disbursement = await services.GetRequiredService<IBillingDisbursementService>().RequestAsync(
                new DisbursementRequestRequest
                {
                    SourceType = DisbursementCodes.LevyPayment,
                    SourceId = sourceId,
                    PayeePartyId = request.PayeePartyId,
                    PayeeAccountId = request.PayeeAccountId,
                    Amount = amount,
                    Method = DisbursementCodes.SepaCreditTransfer,
                    StatementReference = request.StatementReference ?? $"LEVY {request.LevyType} {request.Period}",
                    PurposeText = "Levy payment",
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        if (payable is null)
        {
            payable = new LevyPayableRow { LevyPayableId = Guid.CreateVersion7(), LegalEntityId = legalEntity, LevyAuthority = request.LevyType, Period = request.Period, Currency = currency.Code, RecordVersion = 1 };
            db.LevyPayables.Add(payable);
        }
        else
        {
            payable.RecordVersion++;
        }

        payable.CollectedAmount = amount.Amount;
        payable.PayableAmount = amount.Amount;
        payable.LegalStatus = basis.LegalStatus;
        payable.Provisional = basis.LegalStatus != LevyLegal.Settled;
        payable.PayeePartyId = request.PayeePartyId;
        payable.PayeeAccountId = request.PayeeAccountId;
        payable.DisbursementId = disbursement.DisbursementId;
        payable.SourceId = sourceId;
        payable.RequestedAt = clock.Now;
        payable.RequestedBy = context.Actor.ToString();
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent request created the payable first: the unique (authority, period) index decides.
            return DomainError.Of(ModuleCode.BIL, "DUPLICATE", "A payable for this levy authority and period was created concurrently.");
        }

        return new TaxLevyRequestLevyPaymentResponse
        {
            PayableId = payable.LevyPayableId,
            LevyType = payable.LevyAuthority,
            Period = payable.Period,
            Amount = amount,
            LegalStatus = payable.LegalStatus,
            Provisional = payable.Provisional,
            DisbursementId = disbursement.DisbursementId,
            DisbursementNumber = disbursement.DisbursementNumber,
            Status = Codes.Of(disbursement.Status switch
            {
                DisbursementRequestResponse.StatusValue.Requested => DisbursementState.Requested,
                DisbursementRequestResponse.StatusValue.PendingApproval => DisbursementState.PendingApproval,
                DisbursementRequestResponse.StatusValue.Approved => DisbursementState.Approved,
                DisbursementRequestResponse.StatusValue.Released => DisbursementState.Released,
                DisbursementRequestResponse.StatusValue.Issued => DisbursementState.Issued,
                _ => DisbursementState.Cleared,
            }),
        };
    }

    private static DomainError Duplicate(LevyPayableRow payable) =>
        new(ErrorCode.For(ModuleCode.BIL, "DUPLICATE"), "A payable and payment request for this levy authority and period already exist.")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["existingPayableId"] = payable.LevyPayableId.ToString(),
                ["existingDisbursementId"] = payable.DisbursementId.Value.ToString(),
            },
        };
}

internal sealed class RequestLevyPaymentAuditor : ICommandAuditor<RequestLevyPayment, TaxLevyRequestLevyPaymentResponse>
{
    public CommandAuditFacts Describe(RequestLevyPayment command, Result<TaxLevyRequestLevyPaymentResponse>? result)
    {
        var id = command.Request.LevyType + ":" + command.Request.Period;
        return new CommandAuditFacts
        {
            ObjectRef = new ObjectRef(ModuleCode.BIL, "LevyPayable", id),
            BusinessKeys = BusinessKeys.Empty.With("levyType", command.Request.LevyType).With("period", command.Request.Period),
            Changes = result is { IsSuccess: true }
                ? AuditDiff.Compute(null, new { levyType = command.Request.LevyType, period = command.Request.Period, amount = result.Value.Value.Amount.ToString(), disbursementId = result.Value.Value.DisbursementId.Value })
                : [],
        };
    }
}
