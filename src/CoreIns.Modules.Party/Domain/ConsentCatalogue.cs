namespace CoreIns.Modules.Party.Domain;

/// <summary>
/// Consent purposes, channels and media (BO-PTY-06, REQ-PTY-005). TODO(hardening): read the lists from the MKT code lists of the
/// country pack; the codes below are the neutral defaults.
/// </summary>
internal static class ConsentCatalogue
{
    /// <summary>The preference that applies to every purpose without its own.</summary>
    public const string Default = "DEFAULT";

    public static IReadOnlySet<string> Purposes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "MARKETING", "PROFILING", "THIRD_PARTY_SHARING", "SERVICE_MESSAGES", "CLAIMS_UPDATES", "RENEWAL_NOTICES", "DOCUMENT_DELIVERY",
    };

    /// <summary>Purposes that may only use a channel the party has given consent for; the others only honour a withdrawal or an objection.</summary>
    public static IReadOnlySet<string> ConsentRequired { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "MARKETING", "PROFILING", "THIRD_PARTY_SHARING",
    };

    public static IReadOnlySet<string> Channels { get; } = new HashSet<string>(StringComparer.Ordinal) { "EMAIL", "SMS", "POST", "PHONE", "PORTAL" };

    public static IReadOnlySet<string> Media { get; } = new HashSet<string>(StringComparer.Ordinal) { "DIGITAL", "PAPER" };

    public static string DefaultMedium(string channel) => channel == "POST" ? "PAPER" : "DIGITAL";
}
