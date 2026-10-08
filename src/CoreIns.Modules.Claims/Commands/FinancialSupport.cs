using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>Errors and loaders shared by the claim financial commands.</summary>
internal static class FinancialSupport
{
    public static DomainError Stale(string detail) => DomainError.Of(ModuleCode.CLM, "SET-STALE", detail);

    public static DomainError NotPayable(params string[] reasons) =>
        new(ErrorCode.For(ModuleCode.CLM, "NOT-PAYABLE"), $"The payment cannot be made: {string.Join(", ", reasons)}.")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["reasons"] = string.Join(",", reasons) },
        };

    /// <summary>The set, tracked and row-locked (<c>FOR UPDATE</c>); call after <see cref="ClaimSupport.LoadAsync"/> (lock order claim → set).</summary>
    public static async Task<TransactionSetRow?> LockSetAsync(ClaimsDbContext db, LegalEntityId legalEntity, ClaimTransactionSetId setId, CancellationToken cancellationToken)
    {
        var rows = await db.TransactionSets
            .FromSql($"SELECT * FROM clm.transaction_set WHERE set_id = {setId.Value} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The claim of a set (no lock), to take the claim lock first.</summary>
    public static Task<ClaimId?> ClaimOfSetAsync(ClaimsDbContext db, LegalEntityId legalEntity, ClaimTransactionSetId setId, CancellationToken cancellationToken) =>
        db.TransactionSets.AsNoTracking().Where(s => s.SetId == setId && s.LegalEntityId == legalEntity).Select(s => (ClaimId?)s.ClaimId)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>The set's transactions in sequence order, its lines and payments (tracked: lines and payments move).</summary>
    public static async Task<SetContent> ContentAsync(ClaimsDbContext db, TransactionSetRow set, CancellationToken cancellationToken)
    {
        var transactions = await db.FinancialTransactions.AsNoTracking().Where(t => t.SetId == set.SetId).OrderBy(t => t.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lineIds = transactions.Select(t => t.ReserveLineId).Distinct().ToList();
        var lines = await db.ReserveLines.Where(l => lineIds.Contains(l.ReserveLineId)).ToDictionaryAsync(l => l.ReserveLineId, cancellationToken).ConfigureAwait(false);
        var payments = await db.ClaimPayments.Where(p => p.SetId == set.SetId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new SetContent(set, transactions, lines, payments);
    }
}

/// <summary>A set with its immutable transactions, its lines and its payments.</summary>
internal sealed record SetContent(
    TransactionSetRow Set,
    IReadOnlyList<FinancialTransactionRow> Transactions,
    IReadOnlyDictionary<ReserveLineId, ReserveLineRow> Lines,
    IReadOnlyList<ClaimPaymentRow> Payments)
{
    public LineKey KeyOf(FinancialTransactionRow t)
    {
        var line = Lines[t.ReserveLineId];
        return new LineKey(line.ExposureId, line.CostType, line.CostCategory, line.Currency);
    }

    /// <summary>The canonical transactions of the content hash, recomputed from the stored rows.</summary>
    public IEnumerable<CanonicalTransaction> Canonical() => Transactions.Select(t =>
    {
        var payment = t.ClaimPaymentId is { } id ? Payments.Single(p => p.ClaimPaymentId == id) : null;
        return new CanonicalTransaction(
            t.TxnId, t.TxnNumber, t.Kind, KeyOf(t), t.Amount, t.Eroding, t.PaymentType, t.ClaimPaymentId?.Value,
            payment?.PayeePartyId, payment?.PayeeAccountId, t.ReasonCode, t.Proposed);
    });
}
