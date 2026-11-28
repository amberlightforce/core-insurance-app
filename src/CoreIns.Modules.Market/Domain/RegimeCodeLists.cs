using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.Modules.Market.Domain;

internal sealed record RegimeCodeEntry(string Code, string Name);

/// <summary>A code list of one regime and version. Anything not Settled is served with the fail-closed flag.</summary>
internal sealed record RegimeCodeList(string Regime, string Version, string Name, LegalStatus LegalStatus, string Source, IReadOnlyList<RegimeCodeEntry> Codes)
{
    public bool FailClosed => LegalStatus != LegalStatus.Settled;
}

/// <summary>One crosswalk edge between two regimes (a code of <c>From</c> maps to a code of <c>To</c>).</summary>
internal sealed record RegimeCrosswalkEdge(string FromRegime, string FromCode, string ToRegime, string ToCode);

/// <summary>
/// Regime code lists (BO-MKT-06, REQ-MKT-007): the internal line-of-business codes of the Greece pack and the Solvency II non-life
/// lines of business, with a crosswalk between them. None of it is legally confirmed here, so every list is Unverified (fail closed).
/// </summary>
internal static class RegimeCodeLists
{
    public const string InternalLob = "INTERNAL_LOB";
    public const string SiiLob = "SII_LOB";

    // TODO(hardening): the Solvency II list is typed from memory of Delegated Regulation 2015/35 Annex I; verify it against the regulation, then
    // move both lists and the crosswalk into versioned pack data. Add the regulatory-reporting code lists (EIOPA, BoG) when DAT needs them.
    public static IReadOnlyList<RegimeCodeList> All { get; } =
    [
        new(InternalLob, "2026.1", "Internal lines of business (Greece pack)", LegalStatus.Unverified, "Pack working list, not a statutory source",
        [
            new("MTPL", "Motor third-party liability"), new("MOTOR_OD", "Motor own damage"), new("FIRE_PROPERTY", "Fire and property"),
            new("GENERAL_LIABILITY", "General liability"), new("MARINE_TRANSPORT", "Marine, aviation and transport"), new("LEGAL_EXPENSES", "Legal expenses"),
            new("ASSISTANCE", "Assistance"), new("MEDICAL", "Medical expense"),
        ]),
        new(SiiLob, "2026.1", "Solvency II non-life lines of business", LegalStatus.Unverified, "Delegated Regulation (EU) 2015/35 Annex I (to be verified)",
        [
            new("1", "Medical expense insurance"), new("2", "Income protection insurance"), new("3", "Workers compensation insurance"),
            new("4", "Motor vehicle liability insurance"), new("5", "Other motor insurance"), new("6", "Marine, aviation and transport insurance"),
            new("7", "Fire and other damage to property insurance"), new("8", "General liability insurance"), new("9", "Credit and suretyship insurance"),
            new("10", "Legal expenses insurance"), new("11", "Assistance"), new("12", "Miscellaneous financial loss"),
        ]),
    ];

    private static readonly (string Internal, string Sii)[] Pairs =
    [
        ("MTPL", "4"), ("MOTOR_OD", "5"), ("FIRE_PROPERTY", "7"), ("GENERAL_LIABILITY", "8"), ("MARINE_TRANSPORT", "6"),
        ("LEGAL_EXPENSES", "10"), ("ASSISTANCE", "11"), ("MEDICAL", "1"),
    ];

    /// <summary>Both directions of every pair, so a round trip A to B to A finds the start again.</summary>
    public static IReadOnlyList<RegimeCrosswalkEdge> Crosswalk { get; } =
    [
        .. Pairs.Select(p => new RegimeCrosswalkEdge(InternalLob, p.Internal, SiiLob, p.Sii)),
        .. Pairs.Select(p => new RegimeCrosswalkEdge(SiiLob, p.Sii, InternalLob, p.Internal)),
    ];

    public static RegimeCodeList? Find(string regime) => All.FirstOrDefault(r => string.Equals(r.Regime, regime, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the code is in the regime's list.</summary>
    public static bool IsValid(RegimeCodeList list, string code) => list.Codes.Any(c => string.Equals(c.Code, code, StringComparison.Ordinal));

    /// <summary>The target codes a code maps to; empty when there is no edge.</summary>
    public static IReadOnlyList<string> Map(string fromRegime, string toRegime, string code) =>
    [
        .. Crosswalk.Where(e => string.Equals(e.FromRegime, fromRegime, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.ToRegime, toRegime, StringComparison.OrdinalIgnoreCase) && e.FromCode == code).Select(e => e.ToCode),
    ];
}
