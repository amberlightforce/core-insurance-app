using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Finance.Domain;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>
/// Normalises CLM <c>RecoveryRecorded</c> (PRD-09 events table; REQ-FIN-160, -299; D-SL4-10) into a source entry.
/// The entry type names the recovery type, so the rule set picks the income account per type; the legs are roles:
/// <list type="bullet">
/// <item>SUBROGATION / SALVAGE: Dr <see cref="ClaimLegs.RecoveryClearing"/> / Cr <see cref="ClaimLegs.RecoveryIncome"/> with the claim, the
/// recovery and the counterparty. BIL's RECEIVABLE_COLLECTED later credits the clearing (cash only from BIL); it nets to zero per recovery.
/// CLM publishes the recovery-reserve release as its own ReserveChanged; FIN never infers it.</item>
/// <item>FRIENDLY_SETTLEMENT: Dr <see cref="ClaimLegs.FsClearing"/> (GL-2515) / Cr recovery income, with the statement and the counterparty.</item>
/// </list>
/// Fail closed: an unknown recovery type, a missing counterparty, a missing statement for FS, allocation lines that do not add up,
/// or a functional/group amount that differs from the transaction amount is an intake exception, never a default.
/// </summary>
internal static class RecoveryFacts
{
    public static readonly string RecoveryRecorded = $"clm.{RecoveryRecordedV1.EventType}";

    public static SourceEntry Normalise(string payload, Guid sourceEventId, Guid? claimId, BusinessDate occurredOn)
    {
        var recovery = JsonSerializer.Deserialize<RecoveryRecordedV1>(payload, SharedKernelJson.Options)
            ?? throw new JsonException("RecoveryRecorded payload is empty.");
        var date = recovery.AccountingDate ?? occurredOn;
        var entryType = RecoveryTypes.EntryTypeOf(recovery.RecoveryType);
        var fs = entryType == RecoveryTypes.EntryFriendlySettlement;
        var total = recovery.Amount.Transaction;

        (string Reason, string Detail)? problem = null;
        if (entryType is null)
        {
            problem = (ExceptionReasons.NoRule, $"Unknown recovery type '{recovery.RecoveryType}': there is no posting rule and no default account (REQ-FIN-160).");
        }
        else if (recovery.CounterpartyPartyId.Value == Guid.Empty)
        {
            problem = (ExceptionReasons.InvalidEnvelope, "RecoveryRecorded without a counterparty party: recoveries post with the counterparty dimension (REQ-FIN-160).");
        }
        else if (fs && (recovery.FsStatementId is null || recovery.FsStatementId == Guid.Empty))
        {
            problem = (ExceptionReasons.InvalidEnvelope, "Friendly Settlement RecoveryRecorded without an fsStatementId: GL-2515 nets per statement (REQ-FIN-299).");
        }
        else if (recovery.Amount.Functional != total || recovery.Amount.Group != total)
        {
            problem = (ExceptionReasons.RateMissing, "RecoveryRecorded functional or group amounts differ from the transaction amount; FIN has no FX rates (D-SL2-06).");
        }
        else if (total.IsNegative || total.IsZero)
        {
            problem = (ExceptionReasons.Unbalanced, $"RecoveryRecorded amount {total} must be positive.");
        }
        else if (recovery.AllocationByLine.Count > 0
                 && (recovery.AllocationByLine.Any(l => l.Amount.Transaction.Currency != total.Currency)
                     || Money.Sum(recovery.AllocationByLine.Select(l => l.Amount.Transaction), total.Currency) != total))
        {
            problem = (ExceptionReasons.Unbalanced, $"RecoveryRecorded allocation lines do not add up to the recovery amount {total}.");
        }

        var dimensions = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [LineDimensionKeys.ClaimId] = (recovery.ClaimId?.Value ?? claimId)?.ToString("D"),
            [LineDimensionKeys.RecoveryId] = recovery.RecoveryId.Value.ToString("D"),
            [LineDimensionKeys.CounterpartyPartyId] = recovery.CounterpartyPartyId.Value.ToString("D"),
            [LineDimensionKeys.StatementRef] = fs ? recovery.FsStatementId?.ToString("D") : null,
        };
        return new SourceEntry(
            sourceEventId,
            entryType ?? RecoveryTypes.EntrySubrogation,
            date,
            date,
            [
                new SourceLine(fs ? ClaimLegs.FsClearing : ClaimLegs.RecoveryClearing, Sides.Debit, total, dimensions),
                new SourceLine(ClaimLegs.RecoveryIncome, Sides.Credit, total, dimensions),
            ])
        {
            SourceRef = recovery.RecoveryId.Value.ToString("D"),
            NeedsPolicyContext = false,
            Problem = problem?.Detail,
            ProblemReason = problem?.Reason ?? ExceptionReasons.Unbalanced,
        };
    }
}
