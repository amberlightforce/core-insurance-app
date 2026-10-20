using System.Text.Json;
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

    /// <summary>A page of the entity's disbursements, newest first (cursor = the offset of the next page).</summary>
    public async Task<DisbursementListPage> ListAsync(LegalEntityId legalEntity, string? cursor, int? limit, CancellationToken cancellationToken) =>
        await ListAsync(legalEntity, cursor, limit, DisbursementFilter.None, cancellationToken).ConfigureAwait(false);

    /// <summary>A filtered page (server-side), newest first.</summary>
    public async Task<DisbursementListPage> ListAsync(LegalEntityId legalEntity, string? cursor, int? limit, DisbursementFilter filter, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Disbursements.AsNoTracking().Where(d => d.LegalEntityId == legalEntity);
        if (filter.State is { } state)
        {
            query = query.Where(d => d.State == state);
        }

        if (filter.SourceType is { } sourceType)
        {
            query = query.Where(d => d.SourceType == sourceType);
        }

        if (filter.PayeeAccountId is { } payee)
        {
            query = query.Where(d => d.PayeeAccountId == payee);
        }

        if (filter.RequestedFrom is { } from)
        {
            query = query.Where(d => d.RequestedAt >= from);
        }

        if (filter.RequestedBefore is { } before)
        {
            query = query.Where(d => d.RequestedAt < before);
        }

        var offset = int.TryParse(cursor, out var skip) && skip > 0 ? skip : 0;
        var rows = await query.OrderByDescending(d => d.RequestedAt).ThenByDescending(d => d.DisbursementId).Skip(offset).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        var accounts = page.Select(r => r.PayeeAccountId).Distinct().ToList();
        var last4 = await db.PayeeAccounts.AsNoTracking().Where(a => accounts.Contains(a.PayeeAccountId))
            .ToDictionaryAsync(a => a.PayeeAccountId, a => a.IbanLast4, cancellationToken).ConfigureAwait(false);
        return new DisbursementListPage
        {
            Items = [.. page.Select(r => new DisbursementListItem { Disbursement = JsonSerializer.SerializeToElement(View(r, last4.GetValueOrDefault(r.PayeeAccountId, string.Empty)), JsonSerializerOptions.Web) })],
            NextCursor = rows.Count > size ? (offset + size).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            Limit = size,
        };
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
            StatementReference = row.StatementRef,
            Lines = FsLines.Deserialize(row.Lines),
            ReleaseApproval = row.SourceType == DisbursementCodes.FsClearing
                ? new DisbursementRequestResponse.ReleaseApprovalDetail
                {
                    Required = true,
                    ApprovalRequestId = row.ReleaseApprovalRequestId is { } id ? new ApprovalRequestId(id) : null,
                    RequestedBy = row.CreatedBy,
                }
                : null,
        };
    }
}

/// <summary>Server-side filters of the disbursement list; codes are the stored upper-snake codes, the time range is [from, before).</summary>
internal sealed record DisbursementFilter(string? State, string? SourceType, Guid? PayeeAccountId, Instant? RequestedFrom, Instant? RequestedBefore)
{
    public static DisbursementFilter None { get; } = new(null, null, null, null, null);
}
