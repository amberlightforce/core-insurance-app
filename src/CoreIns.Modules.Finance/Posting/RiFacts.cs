using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Reinsurance.Contracts.Events;
using CoreIns.Platform.Contracts.Common;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>
/// Normalises RI <c>RecoveryCalculated</c> (PRD-09 events table; REQ-FIN-164, D-SL4-10) into a source entry: for the paid delta and for
/// the outstanding delta, separately, Dr <see cref="Sl4Entries.RiHeld"/> (GL-1320, RI held asset for incurred claims) /
/// Cr <see cref="Sl4Entries.RiRecovered"/> (GL-4210, amounts recovered from reinsurers). Each line carries the dimension
/// <c>recoverableBasis</c> PAID or OUTSTANDING, the participant party (as counterparty), the contract and contract year, and the
/// RI-held group UNASSIGNED (FIN IFRS 17 is not built). A negative delta posts on the reversed sides (the mapping flips them).
/// One journal per source event: the event id is the idempotency key, so a replay posts nothing.
/// Fail closed: an incurred delta that is not the sum of the paid and outstanding deltas (a missing basis split), a functional/group
/// amount differing from the transaction amount, or a missing participant is an intake exception.
/// </summary>
internal static class RiFacts
{
    public static readonly string RecoveryCalculated = $"ri.{RecoveryCalculatedV1.EventType}";

    public static SourceEntry Normalise(string payload, Guid sourceEventId, BusinessDate occurredOn)
    {
        var recovery = JsonSerializer.Deserialize<RecoveryCalculatedV1>(payload, SharedKernelJson.Options)
            ?? throw new JsonException("RecoveryCalculated payload is empty.");
        var incurred = recovery.IncurredDelta.Transaction;
        var paid = recovery.PaidDelta.Transaction;
        var outstanding = recovery.OutstandingDelta.Transaction;

        (string Reason, string Detail)? problem = null;
        if (recovery.ParticipantPartyId.Value == Guid.Empty)
        {
            problem = (ExceptionReasons.InvalidEnvelope, "RecoveryCalculated without a participant party (REQ-FIN-164).");
        }
        else if (!Single(recovery.IncurredDelta) || !Single(recovery.PaidDelta) || !Single(recovery.OutstandingDelta))
        {
            problem = (ExceptionReasons.RateMissing, "RecoveryCalculated functional or group amounts differ from the transaction amount; FIN has no FX rates (D-SL2-06).");
        }
        else if (paid.Currency != incurred.Currency || outstanding.Currency != incurred.Currency || paid + outstanding != incurred)
        {
            problem = (ExceptionReasons.Unbalanced,
                $"RecoveryCalculated incurred delta {incurred} is not the paid delta {paid} plus the outstanding delta {outstanding}: the recoverableBasis split is missing (REQ-FIN-164).");
        }

        Dictionary<string, string?> Dimensions(string basis) => new(StringComparer.Ordinal)
        {
            [LineDimensionKeys.ClaimId] = recovery.ClaimIds.Count == 1 ? recovery.ClaimIds[0].Value.ToString("D") : null,
            [LineDimensionKeys.RiContractId] = recovery.RiContractId.Value.ToString("D"),
            [LineDimensionKeys.RiContractYear] = recovery.ContractYear?.ToString(CultureInfo.InvariantCulture),
            [LineDimensionKeys.CounterpartyPartyId] = recovery.ParticipantPartyId.Value.ToString("D"),
            [LineDimensionKeys.RecoverableBasis] = basis,
            [LineDimensionKeys.RiHeldGroup] = Sl4Entries.Unassigned,
        };

        var lines = new List<SourceLine>();
        foreach (var (basis, amount) in new[] { (Sl4Entries.Paid, paid), (Sl4Entries.Outstanding, outstanding) })
        {
            if (amount.IsZero)
            {
                continue;
            }

            lines.Add(new SourceLine(Sl4Entries.RiHeld, Sides.Debit, amount, Dimensions(basis)));
            lines.Add(new SourceLine(Sl4Entries.RiRecovered, Sides.Credit, amount, Dimensions(basis)));
        }

        return new SourceEntry(sourceEventId, Sl4Entries.RiRecovery, occurredOn, occurredOn, lines)
        {
            NeedsPolicyContext = false,
            Problem = problem?.Detail,
            ProblemReason = problem?.Reason ?? ExceptionReasons.Unbalanced,
        };
    }

    private static bool Single(MoneyByCurrency4 amount) => amount.Functional == amount.Transaction && amount.Group == amount.Transaction;
}
