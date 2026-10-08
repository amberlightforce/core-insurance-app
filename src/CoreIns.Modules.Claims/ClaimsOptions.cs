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

    /// <summary>The configured zone.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>The business date of an instant in the legal entity's zone.</summary>
    public BusinessDate DateOf(Instant instant) => instant.ToBusinessDate(Zone);
}
