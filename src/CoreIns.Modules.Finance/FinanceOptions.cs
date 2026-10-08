using System.ComponentModel.DataAnnotations;

namespace CoreIns.Modules.Finance;

/// <summary>
/// Configuration section <c>Finance</c>. A stamp setting, not a regulatory value: the legal entity's time zone, which
/// gives the business date of a posting event that carries no accounting date (REQ-FIN-035; CLM events whose payload
/// predates the typed <c>accountingDate</c>).
/// </summary>
internal sealed class FinanceOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Finance";

    /// <summary>IANA time zone of the stamp's legal entity (e.g. Europe/Athens).</summary>
    [Required]
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>The configured zone.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}
