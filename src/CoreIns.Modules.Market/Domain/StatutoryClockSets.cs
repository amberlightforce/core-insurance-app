using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.Modules.Market.Domain;

/// <summary>
/// One statutory clock value as MKT serves it (BO-MKT-06, REQ-MKT-009). CMP keeps the clock engine and runs the clocks; MKT serves the
/// set with its legal status. A value that is not <see cref="LegalStatus.Settled"/> carries <see cref="FailClosed"/>: callers must not rely on it
/// in Production (D-REG-02, D-REG-04).
/// </summary>
internal sealed record StatutoryClockValue(
    string Code, string Name, string Kind, string Duration, string Jurisdiction, LegalStatus LegalStatus, string Source, string? WarnBefore = null, bool Extendable = false)
{
    /// <summary>True when the value is not legally settled: callers fail closed (refuse in Production).</summary>
    public bool FailClosed => LegalStatus != LegalStatus.Settled;
}

/// <summary>
/// The clock values MKT serves, per jurisdiction. The values mirror the CMP seed definitions (same codes, same durations) and map the CMP
/// tags onto the MKT legal-status vocabulary: VERIFIED to Settled, SECONDARY_SOURCE to Verify, ILLUSTRATIVE and UNVERIFIED to Unverified.
/// Only Greece has values; a jurisdiction with no entry returns an empty set (fail closed, no invented value).
/// </summary>
internal static class StatutoryClockSets
{
    // TODO(hardening): move these values into versioned country-pack data so a change goes through a pack version and the configuration
    // hash, and have CMP read them from here instead of its own seed list.
    private static readonly StatutoryClockValue[] Greece =
    [
        new("BIL_NONPAY_NOTICE", "Non-payment notice waiting period", "WAITING_PERIOD", "P1M", "GR", LegalStatus.Verify, "Law 2496/1997 Art 6 par 2 (PRD-06 R-60, K-01)"),
        new("CMP_DSAR_RESPONSE", "Data subject request deadline", "DEADLINE", "P1M", "GR", LegalStatus.Settled, "GDPR Art 12(3) (PRD-18 section 9.2)", "P7D", Extendable: true),
        new("CLM_FS_COUNTERPARTY_REPLY", "FS counterparty reply", "DEADLINE", "P30D", "GR", LegalStatus.Unverified, "PRD-08 placeholder, not verified", "P5D"),
        new("CLM_OPEN_CLAIM_RESPONSE", "Open claim handling deadline", "DEADLINE", "P30D", "GR", LegalStatus.Unverified, "PRD-07 placeholder, not verified", "P5D"),
        new("CMP_COMPLAINT_REPLY", "Complaint reply deadline", "DEADLINE", "P50D", "GR", LegalStatus.Verify, "BoG Executive Committee Act 88/5.4.2016 (PRD-11 OBL-COMP, OI-CMP-06)", "P7D"),
        new("CLM_MTPL_OFFER", "MTPL reasoned offer deadline", "DEADLINE", "P3M", "GR", LegalStatus.Unverified, "PRD-07 / D-REG-04: 3 months from the claim notice (OQ-008)", "P14D"),
        new("CLM_MTPL_ASSESSMENT", "MTPL damage assessment deadline", "DEADLINE", "P25D", "GR", LegalStatus.Unverified, "PRD-07 / D-REG-04: 15/25 days (OQ-008)", "P5D"),
        new("CLM_MTPL_PAYMENT", "MTPL payment deadline", "DEADLINE", "P10D", "GR", LegalStatus.Unverified, "PRD-07 / D-REG-04: 10 days from proven offer delivery (OQ-008)", "P2D"),
        new("CLM_MTPL_REPAIR", "MTPL repair in kind deadline", "DEADLINE", "P20D", "GR", LegalStatus.Unverified, "PRD-07 / D7: 20 days (OQ-008)", "P3D"),
    ];

    /// <summary>The values of a jurisdiction, optionally one clock code; empty when the jurisdiction has none.</summary>
    public static IReadOnlyList<StatutoryClockValue> Find(string jurisdiction, string? clockCode = null) =>
        string.Equals(jurisdiction, "GR", StringComparison.OrdinalIgnoreCase)
            ? [.. Greece.Where(v => clockCode is null || string.Equals(v.Code, clockCode, StringComparison.Ordinal))]
            : [];
}
