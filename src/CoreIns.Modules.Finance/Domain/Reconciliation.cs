using System.Globalization;
using CoreIns.Rules;

namespace CoreIns.Modules.Finance.Domain;

/// <summary>
/// A reconciliation definition (BO-FIN-08, REQ-FIN-244, -250..-259): the control totals one source module pushes, compared per currency with the
/// movement of ledger accounts in an accounting period. <see cref="Sign"/> is +1 for a debit-normal account (net debit is the source figure) and -1
/// for a credit-normal one.
/// </summary>
internal sealed record ReconDefinition(string Code, string Description, string Source, IReadOnlyList<string> Accounts, int Sign);

/// <summary>The reconciliation definitions FIN ships with, the break kinds and states, and the comparison rule.</summary>
internal static class Reconciliation
{
    public const string BilReceivable = "BIL_RECEIVABLE";
    public const string RiRecoverable = "RI_RECOVERABLE";
    public const string ClaimsPaymentsBank = "CLM_PAYMENTS_BANK";

    public const string Amount = "AMOUNT";
    public const string Count = "COUNT";
    public const string MissingSource = "MISSING_SOURCE";
    public const string MissingLedger = "MISSING_LEDGER";

    public const string Open = "OPEN";
    public const string Accepted = "ACCEPTED";
    public const string Resolved = "RESOLVED";

    public const string Clean = "CLEAN";
    public const string Breaks = "BREAKS";

    /// <summary>How a RESOLVED break was cleared: by hand (match) or because a later run no longer found it.</summary>
    public const string Matched = "MATCHED";
    public const string Cleared = "CLEARED";
    public const string Superseded = "SUPERSEDED";

    // TODO(hardening): resolve the control accounts from FIN posting configuration instead of these constants (the receivable one is also
    // hard-coded in the migration reconciliation), and add tolerance and item-level matching per definition.
    public static readonly IReadOnlyList<ReconDefinition> Definitions =
    [
        new(BilReceivable, "BIL control totals (premium receivable) against the policyholder receivable control account", "BIL", ["GL-1210"], 1),
        new(RiRecoverable, "RI recoverable totals against the reinsurance asset account", "RI", ["GL-1320"], 1),
        new(ClaimsPaymentsBank, "Claims payments in the bank file against the claim payments clearing account", "CLM", ["GL-2510"], -1),
    ];

    public static ReconDefinition? Find(string? code) => Definitions.FirstOrDefault(d => d.Code == code);

    public static bool IsSource(string? source) => Definitions.Any(d => d.Source == source);

    /// <summary>Source figures of one currency (count absent when the source reports none).</summary>
    public sealed record Side(decimal Amount, int? Count);

    /// <summary>One difference found by a comparison.</summary>
    public sealed record Found(string Currency, string Kind, decimal SourceAmount, decimal LedgerAmount, int? SourceCount, int? LedgerCount);

    /// <summary>All money figures are brought to 2 places with the platform rounding before they are compared; a one-cent difference is a break.</summary>
    public static decimal Money(decimal value) => DecimalRounding.Round(value, 2, DecimalRounding.DefaultMoneyMode);

    public static string Text(decimal value) => Money(value).ToString("0.00", CultureInfo.InvariantCulture);

    public static bool TryAmount(string? text, out decimal amount)
    {
        amount = 0m;
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed * 100m % 1m != 0m)
        {
            return false;
        }

        amount = parsed;
        return true;
    }

    /// <summary>
    /// Compares the source and the ledger per currency. A currency only on one side is a missing-source or missing-ledger break; on both sides the
    /// amount and, when the source reports a count, the item count are compared.
    /// </summary>
    public static IReadOnlyList<Found> Compare(IReadOnlyDictionary<string, Side> source, IReadOnlyDictionary<string, Side> ledger)
    {
        var found = new List<Found>();
        foreach (var currency in source.Keys.Union(ledger.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var s = source.GetValueOrDefault(currency);
            var l = ledger.GetValueOrDefault(currency);
            var sourceAmount = s is null ? 0m : Money(s.Amount);
            var ledgerAmount = l is null ? 0m : Money(l.Amount);
            if (s is null && l is not null)
            {
                if (ledgerAmount != 0m || l.Count > 0)
                {
                    found.Add(new Found(currency, MissingSource, 0m, ledgerAmount, null, l.Count));
                }
            }
            else if (l is null && s is not null)
            {
                if (sourceAmount != 0m || s.Count > 0)
                {
                    found.Add(new Found(currency, MissingLedger, sourceAmount, 0m, s.Count, 0));
                }
            }
            else if (s is not null && l is not null)
            {
                if (sourceAmount != ledgerAmount)
                {
                    found.Add(new Found(currency, Amount, sourceAmount, ledgerAmount, s.Count, l.Count));
                }

                if (s.Count is { } sc && sc != l.Count)
                {
                    found.Add(new Found(currency, Count, sourceAmount, ledgerAmount, sc, l.Count));
                }
            }
        }

        return found;
    }
}
