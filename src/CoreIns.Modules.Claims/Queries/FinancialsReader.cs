using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Queries;

/// <summary>
/// Derived claim balances (REQ-CLM-095, -096, -101) and the financial read models. Every balance is computed from the
/// immutable transactions of approved sets (approved at or before the asked record time); nothing is stored as an editable
/// total. Reads run on the scope's connection and transaction, so a command sees its own writes.
/// </summary>
internal sealed class FinancialsReader(ClaimsDbContext db)
{
    private static readonly string[] Effective = [Codes.Of(SetStatus.Approved), Codes.Of(SetStatus.Posted)];
    private static readonly string[] AwaitingDecision = [Codes.Of(SetStatus.Submitted), Codes.Of(SetStatus.PendingApproval)];
    private static readonly string[] PaymentInFlight = [Codes.Of(PaymentStatus.Approved), Codes.Of(PaymentStatus.OnHold), Codes.Of(PaymentStatus.Submitted)];
    private static readonly string Rejected = Codes.Of(PaymentStatus.Rejected);
    private static readonly string Pending = Codes.Of(PaymentStatus.Pending);

    /// <summary>Approved amounts per reserve line of the claim, as of <paramref name="asOf"/> (null = now, everything approved).</summary>
    public async Task<Dictionary<ReserveLineId, LineAmounts>> ApprovedAsync(ClaimId claimId, Instant? asOf, CancellationToken cancellationToken)
    {
        var rows = await (
                from t in db.FinancialTransactions.AsNoTracking()
                join s in db.TransactionSets.AsNoTracking() on t.SetId equals s.SetId
                join p in db.ClaimPayments.AsNoTracking() on t.ClaimPaymentId equals (ClaimPaymentId?)p.ClaimPaymentId into payments
                from p in payments.DefaultIfEmpty()
                where t.ClaimId == claimId && Effective.Contains(s.Status)
                select new { t.ReserveLineId, t.Kind, t.Amount, t.Eroding, s.ApprovedAt, PaymentStatus = p == null ? null : p.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<ReserveLineId, LineAmounts>();
        foreach (var row in rows)
        {
            if ((asOf is { } at && row.ApprovedAt > at) || row.PaymentStatus == Rejected)
            {
                continue;
            }

            var amounts = result.GetValueOrDefault(row.ReserveLineId);
            result[row.ReserveLineId] = amounts.Apply(Codes.Parse<TransactionKind>(row.Kind), row.Amount, row.Eroding ?? false);
        }

        return result;
    }

    /// <summary>The reserve lines of the claim.</summary>
    public Task<List<ReserveLineRow>> LinesAsync(ClaimId claimId, CancellationToken cancellationToken) =>
        db.ReserveLines.AsNoTracking().Where(l => l.ClaimId == claimId).OrderBy(l => l.CreatedAt).ThenBy(l => l.ReserveLineId).ToListAsync(cancellationToken);

    /// <summary>Exposures with a payment pending: in a set awaiting decision, or approved, on hold or submitted to BIL (not yet issued).</summary>
    public async Task<HashSet<ExposureId>> PaymentPendingAsync(ClaimId claimId, CancellationToken cancellationToken)
    {
        var rows = await (
                from p in db.ClaimPayments.AsNoTracking()
                join s in db.TransactionSets.AsNoTracking() on p.SetId equals s.SetId
                where p.ClaimId == claimId
                      && (PaymentInFlight.Contains(p.Status) || (p.Status == Pending && AwaitingDecision.Contains(s.Status)))
                select p.ExposureId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows];
    }

    /// <summary><c>clm.Financials.get</c>: balances by line, exposure and claim as of <paramref name="knownAt"/>.</summary>
    public async Task<FinancialsGetResponse> GetAsync(ClaimId claimId, Instant knownAt, CancellationToken cancellationToken)
    {
        var lines = await LinesAsync(claimId, cancellationToken).ConfigureAwait(false);
        var amounts = await ApprovedAsync(claimId, knownAt, cancellationToken).ConfigureAwait(false);
        var pending = await PaymentPendingAsync(claimId, cancellationToken).ConfigureAwait(false);
        var visible = lines.Where(l => l.CreatedAt <= knownAt).ToList();
        var currency = visible.Select(l => l.Currency).FirstOrDefault() ?? Currency.EUR.Code;
        var byLine = visible.Select(l =>
        {
            var a = amounts.GetValueOrDefault(l.ReserveLineId);
            return new FinancialLineBalance
            {
                ReserveLineId = l.ReserveLineId.Value,
                ExposureId = l.ExposureId,
                CostType = l.CostType,
                CostCategory = l.CostCategory,
                Final = l.FinalFlag,
                Reserved = ClaimMoney.Of(a.Reserved, l.Currency),
                Paid = ClaimMoney.Of(a.Paid, l.Currency),
                OpenReserve = ClaimMoney.Of(a.OpenReserve, l.Currency),
                Incurred = ClaimMoney.Of(a.Incurred, l.Currency),
            };
        }).ToList();
        var exposures = visible.GroupBy(l => l.ExposureId).Select(g =>
        {
            var total = g.Aggregate(LineAmounts.Zero, (sum, l) => sum.Plus(amounts.GetValueOrDefault(l.ReserveLineId)));
            return Balance(total, g.Sum(l => amounts.GetValueOrDefault(l.ReserveLineId).OpenReserve), currency, g.Key, pending.Contains(g.Key));
        }).ToList();
        var claimTotal = visible.Aggregate(LineAmounts.Zero, (sum, l) => sum.Plus(amounts.GetValueOrDefault(l.ReserveLineId)));
        var claimOpen = visible.Sum(l => amounts.GetValueOrDefault(l.ReserveLineId).OpenReserve);
        return new FinancialsGetResponse
        {
            ClaimId = claimId,
            KnownAt = knownAt,
            BalancesByLine = byLine,
            Exposures = exposures,
            Totals = Balance(claimTotal, claimOpen, currency, null, pending.Count > 0),
        };
    }

    /// <summary>The set view with its transactions and their derived statuses.</summary>
    public async Task<TransactionSetView?> SetAsync(LegalEntityId legalEntity, ClaimTransactionSetId setId, CancellationToken cancellationToken)
    {
        var set = await db.TransactionSets.AsNoTracking().SingleOrDefaultAsync(s => s.SetId == setId && s.LegalEntityId == legalEntity, cancellationToken)
            .ConfigureAwait(false);
        if (set is null)
        {
            return null;
        }

        var transactions = await db.FinancialTransactions.AsNoTracking().Where(t => t.SetId == setId).OrderBy(t => t.Sequence).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var lines = await db.ReserveLines.AsNoTracking().Where(l => transactions.Select(t => t.ReserveLineId).Contains(l.ReserveLineId))
            .ToDictionaryAsync(l => l.ReserveLineId, cancellationToken).ConfigureAwait(false);
        var payments = await db.ClaimPayments.AsNoTracking().Where(p => p.SetId == setId).ToDictionaryAsync(p => p.ClaimPaymentId, cancellationToken)
            .ConfigureAwait(false);
        return View(set, transactions, lines, payments);
    }

    public static TransactionSetView View(
        TransactionSetRow set, IReadOnlyList<FinancialTransactionRow> transactions, IReadOnlyDictionary<ReserveLineId, ReserveLineRow> lines,
        IReadOnlyDictionary<ClaimPaymentId, ClaimPaymentRow> payments) => new()
    {
        SetId = set.SetId.Value,
        ClaimId = set.ClaimId,
        Status = Codes.Map<SetStatus, TransactionSetView.StatusValue>(Codes.Parse<SetStatus>(set.Status)),
        ContentHash = Sha256Hash.Parse(set.ContentHash),
        Maker = set.CreatedBy,
        ApprovalRequestId = set.ApprovalRequestId is { } request ? new ApprovalRequestId(request) : null,
        ApprovalPayloadHash = set.ApprovalPayloadHash is { } hash ? Sha256Hash.Parse(hash) : null,
        FourEyes = set.FourEyes,
        RejectionReason = set.RejectionReason,
        CreatedAt = set.CreatedAt,
        SubmittedAt = set.SubmittedAt,
        DecidedAt = set.DecidedAt,
        Transactions = [.. transactions.Select(t => Transaction(set, t, lines[t.ReserveLineId], t.ClaimPaymentId is { } p && payments.TryGetValue(p, out var payment) ? payment : null))],
    };

    /// <summary>The derived status of a transaction: its set's state, then its payment's progression (PRD-07 §7.3.3).</summary>
    public static string StatusOf(TransactionSetRow set, ClaimPaymentRow? payment)
    {
        var state = Codes.Parse<SetStatus>(set.Status);
        return state switch
        {
            SetStatus.Rejected => Codes.Of(PaymentStatus.Rejected),
            SetStatus.Approved or SetStatus.Posted => payment?.Status ?? Codes.Of(PaymentStatus.Approved),
            _ => Codes.Of(PaymentStatus.Pending),
        };
    }

    private static FinancialTransactionView Transaction(TransactionSetRow set, FinancialTransactionRow t, ReserveLineRow line, ClaimPaymentRow? payment) => new()
    {
        TxnId = t.TxnId,
        TxnNumber = t.TxnNumber,
        Kind = Codes.Map<TransactionKind, FinancialTransactionView.KindValue>(Codes.Parse<TransactionKind>(t.Kind)),
        ExposureId = t.ExposureId,
        ReserveLineId = t.ReserveLineId.Value,
        CostType = line.CostType,
        CostCategory = line.CostCategory,
        Amount = ClaimMoney.Of(t.Amount, t.Currency),
        FunctionalAmount = ClaimMoney.Of(t.FunctionalAmount, t.FunctionalCurrency),
        GroupAmount = ClaimMoney.Of(t.GroupAmount, t.GroupCurrency),
        Eroding = t.Eroding,
        PaymentType = t.PaymentType,
        ClaimPaymentId = t.ClaimPaymentId?.Value,
        ReasonCode = t.ReasonCode,
        Proposed = t.Proposed,
        Status = StatusOf(set, payment),
    };

    /// <summary>The claim's payments (newest first).</summary>
    public async Task<IReadOnlyList<ClaimPaymentView>> PaymentsAsync(ClaimId claimId, CancellationToken cancellationToken)
    {
        var rows = await db.ClaimPayments.AsNoTracking().Where(p => p.ClaimId == claimId).OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(Payment)];
    }

    public static ClaimPaymentView Payment(ClaimPaymentRow p) => new()
    {
        ClaimPaymentId = p.ClaimPaymentId.Value,
        SetId = p.SetId.Value,
        ExposureId = p.ExposureId,
        PayeePartyId = p.PayeePartyId,
        PayeeAccountId = p.PayeeAccountId,
        MaskedAccount = p.MaskedAccount,
        Method = p.Method,
        PaymentType = p.PaymentType,
        Amount = ClaimMoney.Of(p.Amount, p.Currency),
        Status = Codes.Map<PaymentStatus, ClaimPaymentView.StatusValue>(Codes.Parse<PaymentStatus>(p.Status)),
        HoldReason = p.HoldReason,
        DisbursementId = p.DisbursementId,
        ApprovalEvidenceRef = p.ApprovalEvidenceRef,
        CreatedAt = p.CreatedAt,
        SubmittedAt = p.SubmittedAt,
        IssuedAt = p.IssuedAt,
        ClearedAt = p.ClearedAt,
    };

    /// <summary>The payee accounts captured on the claim (masked).</summary>
    public async Task<IReadOnlyList<ClaimPayeeAccountView>> PayeeAccountsAsync(ClaimId claimId, CancellationToken cancellationToken)
    {
        var rows = await db.PayeeAccounts.AsNoTracking().Where(a => a.ClaimId == claimId).OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(PayeeAccount)];
    }

    public static ClaimPayeeAccountView PayeeAccount(PayeeAccountViewRow a) => new()
    {
        PayeeAccountId = a.PayeeAccountId,
        PartyId = a.PartyId,
        ClaimId = a.ClaimId,
        MaskedIban = a.MaskedIban,
        VerificationStatus = a.VerificationStatus,
        CoolingOffUntil = a.CoolingOffUntil,
        Change = a.IsChange,
        CapturedAt = a.CreatedAt,
    };

    /// <summary>True when the claim exists in the legal entity.</summary>
    public Task<bool> ClaimExistsAsync(LegalEntityId legalEntity, ClaimId claimId, CancellationToken cancellationToken) =>
        db.Claims.AsNoTracking().AnyAsync(c => c.ClaimId == claimId && c.LegalEntityId == legalEntity, cancellationToken);

    private static FinancialBalance Balance(LineAmounts total, decimal open, string currency, ExposureId? exposure, bool paymentPending) => new()
    {
        ExposureId = exposure,
        Reserved = ClaimMoney.Of(total.Reserved, currency),
        Paid = ClaimMoney.Of(total.Paid, currency),
        OpenReserve = ClaimMoney.Of(open, currency),
        Incurred = ClaimMoney.Of(total.Paid + open, currency),
        PaymentPending = paymentPending,
    };
}
