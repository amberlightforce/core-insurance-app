namespace CoreIns.Modules.Party.Domain;

/// <summary>
/// Party role and relationship catalogues (BO-PTY-03, REQ-PTY-002, REQ-PTY-104). TODO(hardening): read the role list from the
/// MKT code list (country pack) instead of this built-in set; the codes below are the neutral defaults every pack ships.
/// </summary>
internal static class RoleCatalogue
{
    /// <summary>The vendor-master role (REQ-PTY-096): needs an AFM on the party before a payee can be paid as a vendor.</summary>
    public const string Vendor = "VENDOR";

    public static IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "POLICYHOLDER", "INSURED", "BENEFICIARY", "CLAIMANT", "THIRD_PARTY", "PAYEE", "PAYER", "LIENHOLDER", "WITNESS", "DRIVER",
        "CONTACT", "REINSURER", Vendor, "BROKER_CONTACT",
    };

    /// <summary>Directed types that form a hierarchy; adding an edge that closes a cycle is refused.</summary>
    public static IReadOnlySet<string> HierarchicalRelationships { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "PARENT_OF", "SUBSIDIARY_OF", "EMPLOYER_OF", "GUARDIAN_OF",
    };

    public static IReadOnlySet<string> Relationships { get; } = new HashSet<string>(HierarchicalRelationships, StringComparer.Ordinal)
    {
        "SPOUSE_OF", "PARTNER_OF", "SIBLING_OF", "DIRECTOR_OF", "SHAREHOLDER_OF", "BENEFICIAL_OWNER_OF", "ATTORNEY_OF", "MEMBER_OF_HOUSEHOLD",
    };

    /// <summary>Types without direction: (a,b) and (b,a) are the same link, stored once with the lower id first.</summary>
    public static IReadOnlySet<string> Symmetric { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "SPOUSE_OF", "PARTNER_OF", "SIBLING_OF",
    };
}
