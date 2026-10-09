using System.Globalization;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Claims.Domain;

/// <summary>Kinds of claim financial transaction (PRD-07 §7.1). The slice builds Reserve and Payment; recoveries are later.</summary>
internal enum TransactionKind
{
    Reserve,
    RecoveryReserve,
    Recovery,
    Payment,
}

/// <summary>Cost types of a reserve line (REQ-CLM-093). The slice's illustrative motor list uses Indemnity and ExpenseAllocated (D-SL2-04).</summary>
internal enum CostType
{
    Indemnity,
    ExpenseAllocated,
    ExpenseUnallocated,
    StatutoryInterest,
}

/// <summary>Payment types (REQ-CLM-119); the slice builds Partial and Final.</summary>
internal enum PaymentType
{
    Partial,
    Final,
}

/// <summary>TransactionSet states (PRD-07 §7.3.2).</summary>
internal enum SetStatus
{
    Draft,
    Submitted,
    PendingApproval,
    Approved,
    Rejected,
    Posted,
}

/// <summary>ClaimPayment states (PRD-07 §7.3.3, slice subset).</summary>
internal enum PaymentStatus
{
    Pending,
    Approved,
    OnHold,
    Submitted,
    Issued,
    Cleared,
    Rejected,
}

/// <summary>Reason codes the system writes on transactions and sets.</summary>
internal static class FinancialReasons
{
    /// <summary>Reserve increase added because an eroding payment exceeds the open reserve (REQ-CLM-097).</summary>
    public const string AutoAdjust = "AUTO_ADJUST";

    /// <summary>Release of the remaining open reserve on a final payment (REQ-CLM-099).</summary>
    public const string FinalRelease = "FINAL_RELEASE";

    /// <summary>Set rejected because something changed since submit (REQ-CLM-112).</summary>
    public const string SetStale = "SET_STALE";

    /// <summary>Set rejected by the checker in the PLT inbox.</summary>
    public const string ApproverRejected = "APPROVER_REJECTED";

    /// <summary>The PLT request was withdrawn (superseded).</summary>
    public const string Withdrawn = "APPROVAL_WITHDRAWN";

    /// <summary>The PLT approval does not cover the content or authority CLM computes (refused execution).</summary>
    public const string ApprovalMismatch = "APPROVAL_MISMATCH";
}

/// <summary>The approval types and subjects CLM asks PLT for (REQ-CLM-109).</summary>
internal static class ClaimApprovals
{
    /// <summary>A referred set without payment: subject the set, hash the set content hash.</summary>
    public const string TransactionSet = "CLM.TRANSACTION_SET";

    /// <summary>The PLT approval evidence reference BIL verifies (D-SL2-10 d): <c>PLT/ApprovalRequest/{id}</c>.</summary>
    public static string EvidenceOf(ApprovalRequestId requestId) => ObjectRef.For(ModuleCode.PLT, "ApprovalRequest", requestId).ToString();

    /// <summary>The evidence of a set approved within the maker's authority: <c>CLM/TransactionSet/{id}</c>.</summary>
    public static string EvidenceOf(ClaimTransactionSetId setId) => ObjectRef.For(ModuleCode.CLM, "TransactionSet", setId).ToString();

    public static ObjectRef SetSubject(ClaimTransactionSetId setId) => ObjectRef.For(ModuleCode.CLM, "TransactionSet", setId);
}

/// <summary>One line's approved amounts (all in the line currency).</summary>
/// <param name="Reserved">Σ approved reserve transactions.</param>
/// <param name="Paid">Σ approved payments (eroding and non-eroding).</param>
/// <param name="ErodingPaid">Σ approved eroding payments.</param>
internal readonly record struct LineAmounts(decimal Reserved, decimal Paid, decimal ErodingPaid, decimal RecoveryReserved = 0m, decimal Recovered = 0m)
{
    public static LineAmounts Zero => default;

    /// <summary>Σ reserve − Σ eroding payments; may be negative only transiently while validating (never stored).</summary>
    public decimal RawOpen => Reserved - ErodingPaid;

    /// <summary>Open reserve, never below zero (REQ-CLM-095).</summary>
    public decimal OpenReserve => Math.Max(0m, RawOpen);

    /// <summary>Incurred = paid + open reserve (REQ-CLM-096).</summary>
    public decimal Incurred => Paid + OpenReserve;

    public decimal OpenRecoveryReserve => Math.Max(0m, RecoveryReserved - Recovered);

    public decimal NetIncurred => Incurred - Recovered - OpenRecoveryReserve;

    public LineAmounts Apply(TransactionKind kind, decimal amount, bool eroding) => kind switch
    {
        TransactionKind.Reserve => this with { Reserved = Reserved + amount },
        TransactionKind.RecoveryReserve => this with { RecoveryReserved = RecoveryReserved + amount },
        TransactionKind.Recovery => this with { Recovered = Recovered + amount },
        TransactionKind.Payment => this with { Paid = Paid + amount, ErodingPaid = eroding ? ErodingPaid + amount : ErodingPaid },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public LineAmounts Plus(LineAmounts other) => new(Reserved + other.Reserved, Paid + other.Paid, ErodingPaid + other.ErodingPaid, RecoveryReserved + other.RecoveryReserved, Recovered + other.Recovered);
}

/// <summary>The identity of a reserve line (REQ-CLM-093): exposure × cost type × cost category × currency.</summary>
internal readonly record struct LineKey(ExposureId ExposureId, string CostType, string CostCategory, string Currency)
{
    /// <summary>Stable text form used in hashes and events (<c>exposure/costType/category/currency</c>).</summary>
    public override string ToString() => $"{ExposureId.Value:D}/{CostType}/{CostCategory}/{Currency}";
}

/// <summary>A transaction as hashed into the set's canonical content.</summary>
internal sealed record CanonicalTransaction(
    Guid TxnId,
    string TxnNumber,
    string Kind,
    LineKey Line,
    decimal Amount,
    bool? Eroding,
    string? PaymentType,
    Guid? ClaimPaymentId,
    PartyId? PayeePartyId,
    Guid? PayeeAccountId,
    string? ReasonCode,
    bool Proposed,
    RecoveryId? RecoveryId = null);

/// <summary>
/// Content and basis hashes of a transaction set. The content hash (REQ-CLM-109) is SHA-256 over the RFC 8785 canonical
/// JSON of the set id, claim id and every transaction in sequence order (amounts with exactly two decimals). The basis
/// hash fingerprints what the set was built on: the claim and exposure states and the approved balances of every line of
/// the claim (authority amounts depend on exposure totals and the claim's cumulative paid, D-SL2-13); a change between build and approval makes the set stale (REQ-CLM-112).
/// </summary>
internal static class SetHashing
{
    public static Sha256Hash Content(ClaimTransactionSetId setId, ClaimId claimId, IEnumerable<CanonicalTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        var items = new JsonArray();
        foreach (var t in transactions)
        {
            var node = new JsonObject
            {
                ["txnId"] = t.TxnId.ToString("D"),
                ["txnNumber"] = t.TxnNumber,
                ["kind"] = t.Kind,
                ["line"] = t.Line.ToString(),
                ["amount"] = Fixed(t.Amount),
                ["proposed"] = t.Proposed,
            };
            if (t.Eroding is { } eroding)
            {
                node["eroding"] = eroding;
            }

            Add(node, "paymentType", t.PaymentType);
            Add(node, "claimPaymentId", t.ClaimPaymentId?.ToString("D"));
            Add(node, "payeePartyId", t.PayeePartyId?.Value.ToString("D"));
            Add(node, "payeeAccountId", t.PayeeAccountId?.ToString("D"));
            Add(node, "reasonCode", t.ReasonCode);
            Add(node, "recoveryId", t.RecoveryId?.Value.ToString("D"));
            items.Add(node);
        }

        return CanonicalJson.Hash(new JsonObject { ["setId"] = setId.Value.ToString("D"), ["claimId"] = claimId.Value.ToString("D"), ["transactions"] = items });
    }

    public static Sha256Hash Basis(string claimState, IEnumerable<(ExposureId Exposure, string State)> exposures, IEnumerable<(LineKey Line, LineAmounts Amounts)> lines)
    {
        var exposureNodes = new JsonArray();
        foreach (var (exposure, state) in exposures.OrderBy(e => e.Exposure.Value))
        {
            exposureNodes.Add(new JsonObject { ["exposureId"] = exposure.Value.ToString("D"), ["state"] = state });
        }

        var lineNodes = new JsonArray();
        foreach (var (line, amounts) in lines.Where(l => l.Amounts != LineAmounts.Zero).OrderBy(l => l.Line.ToString(), StringComparer.Ordinal))
        {
            var node = new JsonObject
            {
                ["line"] = line.ToString(),
                ["reserved"] = Fixed(amounts.Reserved),
                ["paid"] = Fixed(amounts.Paid),
                ["erodingPaid"] = Fixed(amounts.ErodingPaid),
            };
            if (amounts.RecoveryReserved != 0m || amounts.Recovered != 0m)
            {
                node["recoveryReserved"] = Fixed(amounts.RecoveryReserved);
                node["recovered"] = Fixed(amounts.Recovered);
            }

            lineNodes.Add(node);
        }

        return CanonicalJson.Hash(new JsonObject { ["claim"] = claimState, ["exposures"] = exposureNodes, ["lines"] = lineNodes });
    }

    /// <summary>Two-decimal fixed text (EUR minor units, D-SL2-06).</summary>
    public static string Fixed(decimal amount) => decimal.Round(amount, 2, MidpointRounding.ToEven).ToString("F2", CultureInfo.InvariantCulture);

    private static void Add(JsonObject node, string name, string? value)
    {
        if (value is not null)
        {
            node[name] = value;
        }
    }
}

/// <summary>Money helpers of the slice (EUR only, D-SL2-06: functional and group amounts equal the transaction amount).</summary>
internal static class ClaimMoney
{
    public static Money Eur(decimal amount) => new(decimal.Round(amount, 2, MidpointRounding.ToEven), Currency.EUR);

    public static Money Of(decimal amount, string currency) => new(decimal.Round(amount, 2, MidpointRounding.ToEven), Currency.FromCode(currency));
}
