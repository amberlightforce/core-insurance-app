using System.ComponentModel.DataAnnotations;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Claims;

/// <summary>
/// Configuration section <c>Claims</c>: stamp settings, not regulatory values. The legal entity's zone (loss and notice
/// dates are Athens business dates, D-SLC-13), and two technical defaults until their owners exist: the handling segment
/// (segmentation rules, REQ-CLM-083…088, are a later work package) and the Solvency II line of business published on
/// <c>ExposureCreated</c> (the PFC regulatory mapping, REQ-PFC-005, is not wired yet; the default is an explicit
/// <c>UNMAPPED</c> marker, never an invented value).
/// </summary>
internal sealed class ClaimsOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Claims";

    /// <summary>IANA time zone of the stamp's legal entity.</summary>
    [Required]
    public string TimeZone { get; set; } = "Europe/Athens";

    /// <summary>Handling segment until segmentation exists.</summary>
    [Required]
    [RegularExpression("^[A-Z][A-Z0-9_]{0,63}$")]
    public string DefaultHandlingSegment { get; set; } = "STANDARD";

    /// <summary>Solvency II line of business code until the PFC mapping is wired.</summary>
    [Required]
    [RegularExpression("^[A-Z0-9][A-Z0-9_]{0,63}$")]
    public string UnmappedSiiLob { get; set; } = "UNMAPPED";

    /// <summary>Days either side of the loss date in which another claim on the same policy and cause is a probable duplicate (REQ-CLM-041; 0 = same date).</summary>
    [Range(0, 30)]
    public int DuplicateWindowDays { get; set; }

    /// <summary>Claim financials (SL2-CLM-MONEY): illustrative cost categories and the reserve auto-adjust switch.</summary>
    public ClaimFinancialsOptions Financials { get; set; } = new();

    /// <summary>
    /// Re-verification reason codes per decision (REQ-CLM-058). ILLUSTRATIVE TEST DATA until the claims reason list is
    /// configured: KEEP and ADOPT codes; any other code is refused with CLM-ERR-VALIDATION.
    /// </summary>
    public ReverificationReasonOptions ReverificationReasons { get; set; } = new();

    /// <summary>The configured zone.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>The business date of an instant in the legal entity's zone.</summary>
    public BusinessDate DateOf(Instant instant) => instant.ToBusinessDate(Zone);
}

/// <summary>Configuration section <c>Claims:ReverificationReasons</c>: illustrative reason codes of the KEEP / ADOPT decision.</summary>
internal sealed class ReverificationReasonOptions
{
    /// <summary>Marks the lists as illustrative (never presented as approved values).</summary>
    public bool Illustrative { get; set; } = true;

    /// <summary>Codes accepted for KEEP (empty = the illustrative default).</summary>
    public string[] Keep { get; set; } = [];

    /// <summary>Codes accepted for ADOPT (empty = the illustrative default).</summary>
    public string[] Adopt { get; set; } = [];

    /// <summary>True when <paramref name="code"/> is configured for the decision.</summary>
    public bool Allows(bool adopt, string code) =>
        (adopt ? (Adopt.Length > 0 ? Adopt : ["POLICY_CHANGE_RELEVANT", "COVER_CHANGED"])
               : (Keep.Length > 0 ? Keep : ["POLICY_CHANGE_NOT_RELEVANT", "CORRECTION_ONLY"]))
        .Contains(code, StringComparer.Ordinal);
}

/// <summary>
/// Configuration section <c>Claims:Financials</c>. The cost categories are ILLUSTRATIVE TEST DATA (D-SL2-04; PRD-07 §16.5
/// item 1 is open): cost type → allowed categories. <see cref="AutoAdjustReserve"/> is the REQ-CLM-097 per-line switch
/// (one value for every line in the slice): on, an eroding payment above the open reserve adds the difference as a reserve
/// increase to the same set; off, the set is refused with CLM-ERR-PAYMENT-EXCEEDS-RESERVE.
/// </summary>
internal sealed class ClaimFinancialsOptions
{
    /// <summary>Marks the category list as illustrative (never presented as approved values).</summary>
    public bool Illustrative { get; set; } = true;

    /// <summary>The D-SL2-04 list used when the section configures none.</summary>
    public static IReadOnlyDictionary<string, string[]> DefaultCostCategories { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["INDEMNITY"] = ["VEHICLE_REPAIR", "TOTAL_LOSS"],
        ["EXPENSE_ALLOCATED"] = ["ASSESSOR_FEE"],
    };

    /// <summary>Cost type code → category codes (empty = <see cref="DefaultCostCategories"/>).</summary>
    public Dictionary<string, string[]> CostCategories { get; set; } = new(StringComparer.Ordinal);

    /// <summary>REQ-CLM-097: add a reserve increase when a payment exceeds the open reserve (default on).</summary>
    public bool AutoAdjustReserve { get; set; } = true;

    /// <summary>True when <paramref name="category"/> is configured for <paramref name="costType"/>.</summary>
    public bool Allows(string costType, string category) =>
        (CostCategories.Count > 0 ? CostCategories : DefaultCostCategories).TryGetValue(costType, out var categories)
        && categories.Contains(category, StringComparer.Ordinal);
}
