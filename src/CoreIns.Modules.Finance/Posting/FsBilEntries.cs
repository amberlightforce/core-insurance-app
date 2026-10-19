using CoreIns.Modules.Finance.Domain;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>
/// Slice-4 BIL entries (D-SL4-02, -06, -13): the cash side of recoveries and Friendly Settlement. FIN books cash only from BIL
/// (REQ-FIN-037), so these entries are what close the clearing accounts:
/// <list type="bullet">
/// <item><c>RECEIVABLE_COLLECTED</c>: Dr cash (LA-10) / Cr the receivable's clearing leg. For a CLM salvage or subrogation receivable
/// (source CLM_CLAIM_PAYMENT, with a recovery id) the clearing leg becomes the role <see cref="Sl4Entries.RecoveryClearingRole"/>
/// (recovery clearing, nets to zero per recovery); for an FS net receivable (source FS_CLEARING, statement reference) it becomes
/// LA-24 (GL-2515, nets per counterparty and statement).</item>
/// <item><c>FS_CLEARING_RELEASED</c> / <c>FS_CLEARING_CLEARED</c>: the FS net payable through LA-24, in transit LA-13 and cash LA-10.
/// Every line must carry the statement reference and the counterparty.</item>
/// <item><c>RECEIVABLE_REGISTERED</c>: a memo entry; FIN books nothing (the CLM recovery fact is the accounting fact).</item>
/// <item><c>DISBURSEMENT_REVERSED</c>: mapped by plain rules (the reversal of a stopped, voided or returned disbursement).</item>
/// </list>
/// Fail closed: a collected receivable without its recovery id or statement, or an FS entry line without statement or counterparty,
/// is an intake exception (GL-2515 and the recovery clearing could otherwise not be netted).
/// </summary>
internal static class FsBilEntries
{
    /// <summary>True for entries FIN deliberately does not journalise.</summary>
    public static bool IsMemo(SourceEntry entry) => Sl4Entries.Memo.Contains(entry.EntryType, StringComparer.Ordinal);

    /// <summary>The entry with its clearing legs resolved to roles, or flagged with a problem. Other entry types are returned unchanged.</summary>
    public static SourceEntry Adjust(SourceEntry entry)
    {
        if (entry.EntryType is Sl4Entries.FsClearingReleased or Sl4Entries.FsClearingCleared)
        {
            return entry.Lines.Any(l => l.Dimension(LineDimensionKeys.StatementRef) is null || l.Id(LineDimensionKeys.CounterpartyPartyId) is null)
                ? Refuse(entry, $"{entry.EntryType} line without the statement reference or the counterparty: GL-2515 nets per counterparty and statement (REQ-FIN-299).")
                : entry;
        }

        if (entry.EntryType != Sl4Entries.ReceivableCollected)
        {
            return entry;
        }

        var lines = new List<SourceLine>();
        foreach (var line in entry.Lines)
        {
            if (line.Account == Sl4Entries.CashAccount)
            {
                lines.Add(line);
                continue;
            }

            var sourceType = line.Dimension(LineDimensionKeys.SourceType);
            var statement = line.Dimension(LineDimensionKeys.StatementRef);
            var recovery = line.Id(LineDimensionKeys.RecoveryId);
            if (sourceType == DisbursementSources.FsClearing || (sourceType is null && statement is not null))
            {
                if (statement is null || line.Id(LineDimensionKeys.CounterpartyPartyId) is null)
                {
                    return Refuse(entry, "FS_CLEARING RECEIVABLE_COLLECTED line without the statement reference or the counterparty (REQ-FIN-299).");
                }

                lines.Add(line with { Account = Sl4Entries.FsClearingAccount });
            }
            else if (sourceType == DisbursementSources.ClaimPayment || (sourceType is null && recovery is not null))
            {
                if (recovery is null)
                {
                    return Refuse(entry, "CLM_CLAIM_PAYMENT RECEIVABLE_COLLECTED line without the recovery id: the recovery clearing nets per recovery (REQ-FIN-160).");
                }

                lines.Add(line with { Account = Sl4Entries.RecoveryClearingRole });
            }
            else
            {
                lines.Add(line); // unknown source: no rule matches, the entry is an intake exception (NO_RULE)
            }
        }

        return entry with { Lines = lines };
    }

    private static SourceEntry Refuse(SourceEntry entry, string detail) =>
        entry with { Problem = detail, ProblemReason = ExceptionReasons.InvalidEnvelope };
}
