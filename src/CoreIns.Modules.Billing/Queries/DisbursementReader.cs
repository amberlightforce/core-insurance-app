using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>
/// The read side of payee accounts (<c>bil.PayeeAccount.get</c>, REQ-BIL-345) and disbursements
/// (<c>bil.Disbursement.get</c>, REQ-BIL-213). Every query filters by the caller's legal entity. The IBAN is never
/// decrypted here: views carry the masked form built from the stored last four characters.
/// </summary>
internal sealed class DisbursementReader(BillingDbContext db)
{
    /// <summary>The payee account, optionally only when it is valid on <paramref name="validOn"/>.</summary>
    public async Task<PayeeAccountGetResponse?> PayeeAccountAsync(LegalEntityId legalEntity, Guid id, BusinessDate? validOn, CancellationToken cancellationToken)
    {
        var row = await db.PayeeAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.PayeeAccountId == id && a.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null || (validOn is { } date && (date < row.ValidFrom || (row.ValidTo is { } to && date >= to))))
        {
            return null;
        }

        return new PayeeAccountGetResponse
        {
            PayeeAccountId = row.PayeeAccountId,
            PartyId = row.PartyId,
            Purpose = row.Purpose,
            MaskedIban = IbanMask.Mask(row.IbanLast4),
            HolderName = row.HolderName,
            Source = row.Source,
            Status = Codes.Api<PayeeAccountGetResponse.StatusValue>(Codes.Parse<PayeeAccountStatus>(row.Status)),
            VerificationStatus = PayeeAccountMapping.Api(Codes.Parse<PayeeVerification>(row.VerificationStatus)),
            VopResult = row.VopResult is { } vop ? Codes.Api<PayeeAccountGetResponse.VopResultValue>(Codes.Parse<VopOutcome>(vop)) : null,
            VopCheckedAt = row.VopCheckedAt,
            ValidPeriod = Period(row.ValidFrom, row.ValidTo),
            CoolingOffUntil = row.CoolingOffUntil,
            Change = row.IsChange,
        };
    }

    /// <summary>The valid period; an account superseded on the day it was registered shows that one day.</summary>
    private static DateRange Period(BusinessDate from, BusinessDate? to) =>
        to is null ? DateRange.Open(from) : DateRange.Of(from, to.Value > from ? to.Value : from.AddDays(1));

    /// <summary>The disbursement with its state, dates, method and masked payee account.</summary>
    public async Task<DisbursementRequestResponse?> DisbursementAsync(LegalEntityId legalEntity, DisbursementId id, CancellationToken cancellationToken)
    {
        var row = await db.Disbursements.AsNoTracking()
            .SingleOrDefaultAsync(d => d.DisbursementId == id && d.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var last4 = await db.PayeeAccounts.AsNoTracking().Where(a => a.PayeeAccountId == row.PayeeAccountId).Select(a => a.IbanLast4)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        return View(row, last4);
    }

    /// <summary>The contract view of a disbursement row.</summary>
    public static DisbursementRequestResponse View(DisbursementRow row, string ibanLast4)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new DisbursementRequestResponse
        {
            DisbursementId = row.DisbursementId,
            DisbursementNumber = DisbursementNumber.Parse(row.DisbursementNumber),
            Status = Codes.Api<DisbursementRequestResponse.StatusValue>(Codes.Parse<DisbursementState>(row.State)),
            SourceModule = Enum.Parse<ModuleCode>(row.SourceModule),
            SourceType = row.SourceType,
            SourceId = row.SourceId,
            ClaimId = row.ClaimId,
            PayeePartyId = row.PayeePartyId,
            PayeeAccountId = row.PayeeAccountId,
            MaskedPayeeAccount = IbanMask.Mask(ibanLast4),
            Amount = new Money(row.Amount, Currency.FromCode(row.Currency)),
            Method = row.Method,
            RequestedValueDate = row.RequestedValueDate,
            ValueDate = row.ValueDate,
            ApprovalEvidenceRef = row.ApprovalEvidenceRef,
            RequestedAt = row.RequestedAt,
            ReleasedAt = row.ReleasedAt,
            IssuedAt = row.IssuedAt,
            ClearedAt = row.ClearedAt,
        };
    }
}
