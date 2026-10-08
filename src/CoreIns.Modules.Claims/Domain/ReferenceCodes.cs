namespace CoreIns.Modules.Claims.Domain;

/// <summary>Code lists the slice validates against. Values come from the PRD text, never invented.</summary>
internal static class ReferenceCodes
{
    /// <summary>Shared channel code list (R-84, REQ-CLM-036).</summary>
    public static IReadOnlySet<string> Channels { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "STAFF", "CONTACT_CENTRE", "WEB_DIRECT", "APP", "BROKER_PORTAL", "AGENT_PORTAL", "BANK_BRANCH", "BANK_EMBEDDED", "PARTNER_API", "AI_AGENT",
    };

    /// <summary>Receipt media (REQ-CLM-036; SCR-CLM-01 field list).</summary>
    public static IReadOnlySet<string> ReceiptMedia { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "TELEPHONE", "EMAIL", "LETTER", "FAX", "SMS", "IN_PERSON", "ELECTRONIC",
    };

    /// <summary>
    /// Channels the slice accepts at FNOL: the staff channel only (SL2-CLM-CORE scope). Partner/portal channels need the
    /// consent and Draft-confirmation rules of REQ-CLM-038 (later work package).
    /// </summary>
    public static IReadOnlySet<string> SliceChannels { get; } = new HashSet<string>(StringComparer.Ordinal) { "STAFF", "CONTACT_CENTRE" };

    /// <summary>Lines of business the slice accepts (D-SL2-01: motor).</summary>
    public static IReadOnlySet<string> SliceLines { get; } = new HashSet<string>(StringComparer.Ordinal) { "MOTOR" };

    /// <summary>Match reasons of a duplicate candidate (REQ-CLM-041).</summary>
    public const string SamePolicy = "SAME_POLICY";

    /// <summary>Same loss date (Athens business date).</summary>
    public const string SameLossDate = "SAME_LOSS_DATE";

    /// <summary>Same loss cause.</summary>
    public const string SameLossCause = "SAME_LOSS_CAUSE";
}
