using System.ComponentModel.DataAnnotations;

namespace CoreIns.Modules.Reinsurance;

/// <summary>
/// Configuration section <c>Reinsurance</c>: stamp settings, not regulatory values. The legal entity's zone (a contract
/// period is a range of Athens business dates, REQ-RI-058, D-SLC-13) and the interval of the lifecycle scanner.
/// </summary>
internal sealed class ReinsuranceOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Reinsurance";

    /// <summary>IANA time zone of the stamp's legal entity.</summary>
    [Required]
    public string TimeZone { get; set; } = "Europe/Athens";

    /// <summary>Minutes between runs of the lifecycle scanner (activation at the period start, expiry at the period end).</summary>
    [Range(1, 60)]
    public int ScannerIntervalMinutes { get; set; } = 5;

    /// <summary>The zone.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}
