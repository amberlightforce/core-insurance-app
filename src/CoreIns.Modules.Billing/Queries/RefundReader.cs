using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using ApiRefundState = CoreIns.Modules.Billing.Contracts.Api.RefundState;
using StoredRefundState = CoreIns.Modules.Billing.Contracts.RefundState;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>
/// The read side of refunds (<c>bil.Refund.get/list</c>, REQ-BIL-184): the breakdown by charge type with the treatment fields,
/// the netting applied, the payee with the masked IBAN (never the IBAN, REQ-BIL-345) and the lifecycle. Every query filters by
/// the caller's legal entity. No personal data other than the payee's party id.
/// </summary>
internal sealed class RefundReader(BillingDbContext db)
{
    public async Task<RefundView?> GetAsync(LegalEntityId legalEntity, RefundId id, CancellationToken cancellationToken)
    {
        var row = await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.RefundId == id && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (await ViewsAsync([row], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<RefundListPage> ListAsync(
        LegalEntityId legalEntity, BillingAccountId? account, PolicyId? policy, ApiRefundState? state, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Refunds.AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        if (account is { } a)
        {
            query = query.Where(r => r.BillingAccountId == a);
        }

        if (state is { } s)
        {
            var code = Codes.Of(Enum.Parse<StoredRefundState>(s.ToString()));
            query = query.Where(r => r.State == code);
        }

        if (policy is { } p)
        {
            query = query.Where(r => db.RefundCredits.Any(c => c.RefundId == r.RefundId && c.PolicyId == p));
        }

        // Refund ids are UUIDv7: ordering by id is creation order and a stable cursor.
        var all = (await query.ToListAsync(cancellationToken).ConfigureAwait(false)).OrderBy(r => r.RefundId.Value).ToList();
        if (cursor is not null && Guid.TryParse(cursor, out var after))
        {
            all = [.. all.Where(r => r.RefundId.Value.CompareTo(after) > 0)];
        }

        var page = all.Take(size + 1).ToList();
        var shown = page.Take(size).ToList();
        var views = await ViewsAsync(shown, cancellationToken).ConfigureAwait(false);
        return new RefundListPage
        {
            Items = [.. views.Select(v => new RefundListItem { Refund = v })],
            NextCursor = page.Count > size ? shown[^1].RefundId.Value.ToString("D") : null,
            Limit = size,
        };
    }

    /// <summary>The view of already loaded rows (used after a command, inside its transaction).</summary>
    public async Task<List<RefundView>> ViewsAsync(IReadOnlyList<RefundRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.RefundId).ToList();
        var credits = await db.RefundCredits.AsNoTracking().Where(c => ids.Contains(c.RefundId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var nettings = await db.RefundNettings.AsNoTracking().Where(n => ids.Contains(n.RefundId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var itemIds = credits.Select(c => c.CreditItemId).ToList();
        var items = await db.InvoiceItems.AsNoTracking().Where(i => itemIds.Contains(i.InvoiceItemId)).ToDictionaryAsync(i => i.InvoiceItemId, cancellationToken).ConfigureAwait(false);
        var accountIds = rows.Select(r => r.PayeeAccountId).Distinct().ToList();
        var accounts = await db.PayeeAccounts.AsNoTracking().Where(a => accountIds.Contains(a.PayeeAccountId))
            .ToDictionaryAsync(a => a.PayeeAccountId, cancellationToken).ConfigureAwait(false);
        return
        [
            .. rows.Select(r =>
            {
                var currency = Currency.FromCode(r.Currency);
                var account = accounts[r.PayeeAccountId];
                var mine = credits.Where(c => c.RefundId == r.RefundId).OrderBy(c => c.RefundCreditId).ToList();
                return new RefundView
                {
                    RefundId = r.RefundId,
                    BillingAccountId = r.BillingAccountId,
                    State = Codes.Api<ApiRefundState>(Codes.Parse<StoredRefundState>(r.State)),
                    ApprovalState = Codes.Api<RefundApprovalState>(Codes.Parse<RefundApprovalStateCode>(r.ApprovalState)),
                    Amount = new Money(r.Amount, currency),
                    Breakdown = [.. mine.Select(c => Line(c, items[c.CreditItemId], currency))],
                    Netting =
                    [
                        .. nettings.Where(n => n.RefundId == r.RefundId).GroupBy(n => n.TargetInvoiceId).OrderBy(g => g.Key.Value)
                            .Select(g => new RefundNettingLine
                            {
                                Kind = RefundNettingLine.KindValue.OpenInvoice,
                                InvoiceId = g.Key,
                                Amount = new Money(g.Sum(n => n.Amount), currency),
                            }),
                    ],
                    Payee = new RefundPayee
                    {
                        PayeePartyId = r.PayeePartyId,
                        PayeeAccountId = r.PayeeAccountId,
                        MaskedIban = IbanMask.Mask(account.IbanLast4),
                        VerificationStatus = PayeeAccountMapping.Api(Codes.Parse<PayeeVerification>(account.VerificationStatus)),
                    },
                    PayoutMethod = r.PayoutMethod,
                    ReasonCode = r.ReasonCode,
                    SourcePolicyIds = [.. mine.Select(c => c.PolicyId).Distinct()],
                    RequestedBy = RefundContent.ActorGuid(r.RequestedBy),
                    DecidedBy = r.DecidedBy is null ? null : RefundContent.ActorGuid(r.DecidedBy),
                    DecidedAt = r.DecidedAt,
                    DecisionComment = r.DecisionComment,
                    DisbursementId = r.DisbursementId,
                    ProposedAt = r.ProposedAt,
                    RecordVersion = r.RecordVersion,
                };
            }),
        ];
    }

    private static RefundBreakdownLine Line(RefundCreditRow credit, InvoiceItemRow item, Currency currency) => new()
    {
        PolicyId = credit.PolicyId,
        PolicyTermId = credit.TermId,
        TransactionId = credit.TransactionId,
        CreditNoteId = credit.CreditNoteId.Value,
        ChargeType = credit.ChargeType,
        ChargeCategory = credit.ChargeCategory,
        TransactionKind = item.TransactionKind,
        CancellationSource = item.CancellationSource,
        TreatmentRuleId = item.TreatmentRuleId,
        LegalStatus = item.LegalStatus,
        Provisional = item.LegalStatus is null ? null : item.Provisional,
        Amount = new Money(credit.Amount, currency),
    };
}
