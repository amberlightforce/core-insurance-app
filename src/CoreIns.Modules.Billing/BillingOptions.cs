using System.ComponentModel.DataAnnotations;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Billing;

/// <summary>
/// Configuration section <c>Billing</c>. Stamp settings, not regulatory values: the legal entity's time zone (business
/// and accounting dates) and the account currency used when a term arrives before any of its charges (PolicyBound
/// carries no currency).
/// </summary>
internal sealed class BillingOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Billing";

    /// <summary>IANA time zone of the stamp's legal entity (e.g. Europe/Athens).</summary>
    [Required]
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>ISO 4217 currency of a new account when the term's charges have not arrived yet.</summary>
    [Required]
    [RegularExpression("^[A-Z]{3}$")]
    public string DefaultCurrency { get; set; } = string.Empty;

    /// <summary>The configured zone.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>The configured default currency.</summary>
    public Currency Currency => Currency.FromCode(DefaultCurrency);
}
