using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoreIns.Platform.Authority;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Billing.Domain;

/// <summary>Approval state of a refund (contract <c>RefundApprovalState</c>): NOT_REQUIRED within the auto limit and for an unchanged payee.</summary>
internal enum RefundApprovalStateCode
{
    NotRequired,
    Pending,
    Approved,
    Rejected,
}

/// <summary>A credit item with credit left to use: item amount minus Σ credit applications (REQ-BIL-073).</summary>
internal sealed record CreditSource(
    Guid CreditItemId,
    InvoiceId CreditNoteId,
    PolicyId PolicyId,
    PolicyTermId TermId,
    PolicyTransactionId TransactionId,
    string ChargeType,
    string ChargeCategory,
    decimal Remaining);

/// <summary>An open invoice item that credit can be netted against (REQ-BIL-182).</summary>
internal sealed record DebitTarget(InvoiceId InvoiceId, Guid InvoiceItemId, PolicyTermId TermId, BusinessDate DueDate, string InvoiceNumber, int LineNo, decimal Open);

/// <summary>Credit set against an open debit item.</summary>
internal sealed record NettingPair(CreditSource Credit, DebitTarget Target, decimal Amount);

/// <summary>Credit paid out.</summary>
internal sealed record RefundLine(CreditSource Credit, decimal Amount);

/// <summary>The netting and the refund lines of a proposal.</summary>
internal sealed record RefundPlan(IReadOnlyList<NettingPair> Netting, IReadOnlyList<RefundLine> Lines)
{
    public decimal Netted => Netting.Sum(n => n.Amount);

    public decimal Total => Lines.Sum(l => l.Amount);
}

/// <summary>
/// Pure netting and refund calculation (REQ-BIL-181, -182): the credit left on the account is first set against open debit
/// items — items of the same term as the credit first, then the other terms of the account, then by due date, invoice
/// number and line — and only what remains is refunded. Deterministic, so the same state gives the same plan.
/// </summary>
internal static class RefundPlanner
{
    public static RefundPlan Plan(IReadOnlyList<CreditSource> credits, IReadOnlyList<DebitTarget> debits)
    {
        ArgumentNullException.ThrowIfNull(credits);
        ArgumentNullException.ThrowIfNull(debits);
        var remaining = credits.Where(c => c.Remaining > 0m).OrderBy(c => c.TermId.Value).ThenBy(c => c.CreditNoteId.Value).ThenBy(c => c.CreditItemId)
            .ToDictionary(c => c.CreditItemId, c => c.Remaining);
        var ordered = credits.Where(c => c.Remaining > 0m).OrderBy(c => c.TermId.Value).ThenBy(c => c.CreditNoteId.Value).ThenBy(c => c.CreditItemId).ToList();
        var creditTerms = ordered.Select(c => c.TermId).ToHashSet();
        var netting = new List<NettingPair>();
        foreach (var debit in debits
                     .Where(d => d.Open > 0m)
                     .OrderBy(d => creditTerms.Contains(d.TermId) ? 0 : 1)
                     .ThenBy(d => d.DueDate)
                     .ThenBy(d => d.InvoiceNumber, StringComparer.Ordinal)
                     .ThenBy(d => d.LineNo))
        {
            var open = debit.Open;
            foreach (var credit in ordered.OrderBy(c => c.TermId == debit.TermId ? 0 : 1))
            {
                if (open <= 0m)
                {
                    break;
                }

                var take = Math.Min(open, remaining[credit.CreditItemId]);
                if (take <= 0m)
                {
                    continue;
                }

                netting.Add(new NettingPair(credit, debit, take));
                remaining[credit.CreditItemId] -= take;
                open -= take;
            }
        }

        var lines = ordered.Where(c => remaining[c.CreditItemId] > 0m).Select(c => new RefundLine(c, remaining[c.CreditItemId])).ToList();
        return new RefundPlan(netting, lines);
    }
}

/// <summary>Hashes and ids of a refund.</summary>
internal static class RefundContent
{
    /// <summary>
    /// The business reference of the credit a refund pays out (SHA-256 over the sorted credit item ids and amounts): two
    /// refunds of the same credit are the same payment (REQ-BIL-202, PITFALLS 9).
    /// </summary>
    public static string CreditSetKey(IEnumerable<RefundLine> lines)
    {
        var text = string.Join(
            ";",
            lines.OrderBy(l => l.Credit.CreditItemId).Select(l => l.Credit.CreditItemId.ToString("D") + "=" + l.Amount.ToString("F4", CultureInfo.InvariantCulture)));
        return Sha256Hash.ComputeUtf8(text).Value;
    }

    /// <summary>A stable Guid for an actor: the directory object id when the actor id is one, otherwise a hash of the actor key.</summary>
    public static Guid ActorGuid(string actorKey)
    {
        var id = actorKey.Contains(':', StringComparison.Ordinal) ? actorKey[(actorKey.IndexOf(':', StringComparison.Ordinal) + 1)..] : actorKey;
        if (Guid.TryParse(id, out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(actorKey));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

/// <summary>The authority dimensions of a refund (REQ-BIL-188), computed by BIL from its own data, never from the client.</summary>
internal static class RefundAuthority
{
    public static Dictionary<string, DimensionValue> Dimensions(Money amount, bool payeeChanged, string reason) => new(StringComparer.Ordinal)
    {
        [SupportAuthorityTypes.AmountDimension] = DimensionValue.Of(amount),
        [SupportAuthorityTypes.CurrencyDimension] = DimensionValue.OfCodes(amount.Currency.Code),
        [SupportAuthorityTypes.PayeeChangedDimension] = DimensionValue.OfCodes(SupportAuthorityTypes.PayeeChangedCode(payeeChanged)),
        [SupportAuthorityTypes.ReasonDimension] = DimensionValue.OfCodes(reason),
    };

    /// <summary>The code dimensions carried in the PLT approval request (the amount travels as its money field).</summary>
    public static Dictionary<string, string> ApprovalCodes(Money amount, bool payeeChanged, string reason) => new(StringComparer.Ordinal)
    {
        [SupportAuthorityTypes.CurrencyDimension] = amount.Currency.Code,
        [SupportAuthorityTypes.PayeeChangedDimension] = SupportAuthorityTypes.PayeeChangedCode(payeeChanged),
        [SupportAuthorityTypes.ReasonDimension] = reason,
    };
}
