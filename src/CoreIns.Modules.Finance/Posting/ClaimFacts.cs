using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Platform.Contracts.Common;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>
/// Normalises the CLM posting sources (PRD-09 events table; REQ-FIN-034, -037, -158, -159; D-SL2-08) into source
/// entries the data-driven rules post. CLM events carry no sub-ledger accounts, so each fact becomes a fixed pair of
/// legs per line whose source accounts are <see cref="ClaimLegs"/> roles; the rule set maps every role to a book account:
/// <list type="bullet">
/// <item><c>ReserveChanged</c> (entry type = reserve kind): Dr <see cref="ClaimLegs.Incurred"/> / Cr
/// <see cref="ClaimLegs.CaseReserve"/> by the delta (a negative delta posts on the opposite sides). A recovery reserve
/// uses Dr <see cref="ClaimLegs.RecoveryReserve"/> / Cr <see cref="ClaimLegs.Incurred"/>, which has no rule in the slice.</item>
/// <item><c>PaymentIssued</c> (entry type PAYMENT): per payment line, Dr <see cref="ClaimLegs.CaseReserve"/> (eroding;
/// <see cref="ClaimLegs.PaidNonEroding"/> otherwise) / Cr <see cref="ClaimLegs.PaymentClearing"/>, with the claim
/// payment id on every line so GL-2510 can be netted per payment against BIL's DISBURSEMENT_RELEASED.</item>
/// </list>
/// Amounts are the transaction-currency amounts (EUR only in the slice, D-SL2-06; another currency is refused by the
/// posting step as RATE_MISSING). Dimensions are ids and codes only — no payee or other personal data.
/// </summary>
internal static class ClaimFacts
{
    public static readonly string ReserveChanged = $"clm.{ReserveChangedV1.EventType}";

    public static readonly string PaymentIssued = $"clm.{PaymentIssuedV1.EventType}";

    /// <summary>True for the CLM events FIN posts.</summary>
    public static bool IsClaimSource(string registryName) => registryName == ReserveChanged || registryName == PaymentIssued;

    /// <summary>
    /// The source entry of a CLM posting event. <paramref name="claimId"/> is the envelope aggregate (the claim), used
    /// when the payload predates the typed <c>claimId</c>; <paramref name="occurredOn"/> is the event's business date in
    /// the entity zone, used when the payload carries no accounting date.
    /// </summary>
    public static SourceEntry Normalise(string registryName, Guid sourceEventId, string payload, Guid? claimId, BusinessDate occurredOn)
    {
        if (registryName == ReserveChanged)
        {
            var reserve = JsonSerializer.Deserialize<ReserveChangedV1>(payload, SharedKernelJson.Options)
                ?? throw new JsonException("ReserveChanged payload is empty.");
            return Reserve(reserve, sourceEventId, claimId, occurredOn);
        }

        if (registryName == PaymentIssued)
        {
            var payment = JsonSerializer.Deserialize<PaymentIssuedV1>(payload, SharedKernelJson.Options)
                ?? throw new JsonException("PaymentIssued payload is empty.");
            return Payment(payment, claimId, occurredOn);
        }

        throw new ArgumentException($"{registryName} is not a CLM posting source.", nameof(registryName));
    }

    private static SourceEntry Reserve(ReserveChangedV1 reserve, Guid sourceEventId, Guid? claimId, BusinessDate occurredOn)
    {
        var recovery = reserve.Kind == ReserveChangedV1.KindValue.RecoveryReserve;
        var dimensions = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [LineDimensionKeys.ClaimId] = Text(reserve.ClaimId?.Value ?? claimId),
            [LineDimensionKeys.ExposureId] = Text(reserve.ExposureId.Value),
            [LineDimensionKeys.ReserveLineId] = Text(reserve.ReserveLineId),
            [LineDimensionKeys.CostType] = reserve.ReserveLine.CostType,
            [LineDimensionKeys.CostCategory] = reserve.ReserveLine.Category,
            [LineDimensionKeys.PolicyTermId] = Text(reserve.PolicyTermId.Value),
            [LineDimensionKeys.ProductCode] = reserve.ProductCode,
        };
        var amount = reserve.Delta.Transaction;
        var date = reserve.AccountingDate ?? occurredOn;
        var problem = SingleCurrency(reserve.Delta) && SingleCurrency(reserve.NewOpenAmount)
            ? null
            : ((string, string)?)(ExceptionReasons.RateMissing, "ReserveChanged functional or group amounts differ from the transaction amount; FIN has no FX rates (D-SL2-06).");
        return new SourceEntry(
            sourceEventId,
            recovery ? ClaimLegs.RecoveryReserveKind : ClaimLegs.Reserve,
            date,
            date,
            [
                new SourceLine(recovery ? ClaimLegs.RecoveryReserve : ClaimLegs.Incurred, Sides.Debit, amount, dimensions),
                new SourceLine(recovery ? ClaimLegs.Incurred : ClaimLegs.CaseReserve, Sides.Credit, amount, dimensions),
            ])
        {
            NeedsPolicyContext = false,
            Problem = problem?.Item2,
            ProblemReason = problem?.Item1 ?? ExceptionReasons.Unbalanced,
        };
    }

    private static SourceEntry Payment(PaymentIssuedV1 payment, Guid? claimId, BusinessDate occurredOn)
    {
        // Fail closed (D-SL2-12a): never guess a missing line, an unknown eroding flag or an FX conversion.
        var total = payment.Amount.Transaction;
        (string Reason, string Detail)? problem = null;
        if (payment.Lines.Count == 0)
        {
            problem = (ExceptionReasons.InvalidEnvelope, "PaymentIssued has no lines: the reserve lines paid are unknown.");
        }
        else if (payment.Lines.Any(l => l.Eroding is null))
        {
            problem = (ExceptionReasons.InvalidEnvelope, "PaymentIssued line without the eroding flag: whether it consumes the reserve is unknown.");
        }
        else if (!SingleCurrency(payment.Amount) || payment.Lines.Any(l => !SingleCurrency(l.Amount)))
        {
            problem = (ExceptionReasons.RateMissing, "PaymentIssued functional or group amounts differ from the transaction amount; FIN has no FX rates (D-SL2-06).");
        }
        else if (payment.Lines.Any(l => l.Amount.Transaction.Currency != total.Currency)
                 || Money.Sum(payment.Lines.Select(l => l.Amount.Transaction), total.Currency) != total)
        {
            problem = (ExceptionReasons.Unbalanced, $"PaymentIssued lines do not add up to the payment amount {total}.");
        }

        // A Friendly Settlement payment (method CLEARING) settles through the FS clearing account per statement
        // (REQ-FIN-299), never the per-payment GL-2510: its own entry type has no rule in the slice, so it suspends.
        var entryType = payment.Method == ClaimLegs.FsClearingMethod ? ClaimLegs.PaymentFsClearing : ClaimLegs.Payment;
        var lines = new List<SourceLine>();
        foreach (var line in payment.Lines)
        {
            var dimensions = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [LineDimensionKeys.ClaimId] = Text(payment.ClaimId?.Value ?? claimId),
                [LineDimensionKeys.ExposureId] = Text(line.ExposureId?.Value),
                [LineDimensionKeys.ReserveLineId] = Text(line.ReserveLineId),
                [LineDimensionKeys.CostType] = line.CostType,
                [LineDimensionKeys.CostCategory] = line.CostCategory,
                [LineDimensionKeys.ClaimPaymentId] = Text(payment.PaymentId.Value),
                [LineDimensionKeys.DisbursementId] = Text(payment.DisbursementId?.Value), // null for method CLEARING (SL4-CONTRACTS)
            };
            var amount = line.Amount.Transaction;
            lines.Add(new SourceLine(line.Eroding == false ? ClaimLegs.PaidNonEroding : ClaimLegs.CaseReserve, Sides.Debit, amount, dimensions));
            lines.Add(new SourceLine(ClaimLegs.PaymentClearing, Sides.Credit, amount, dimensions));
        }

        var date = payment.AccountingDate ?? occurredOn;
        return new SourceEntry(payment.PaymentId.Value, entryType, date, date, lines)
        {
            SourceRef = payment.PaymentId.Value.ToString("D"),
            NeedsPolicyContext = false,
            Problem = problem?.Detail,
            ProblemReason = problem?.Reason ?? ExceptionReasons.Unbalanced,
        };
    }

    /// <summary>EUR-only slice (D-SL2-06): functional and group amounts must equal the transaction amount.</summary>
    private static bool SingleCurrency(MoneyByCurrency3 amount) => amount.Functional == amount.Transaction && amount.Group == amount.Transaction;

    private static string? Text(Guid? id) => id?.ToString("D");
}