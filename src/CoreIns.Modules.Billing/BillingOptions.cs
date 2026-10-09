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

    /// <summary>
    /// Cooling-off days of a new or changed payee account, <c>bil.payee.cooling_off.&lt;purpose&gt;</c> (PRD-06 §10.2
    /// default 30, one value for every purpose until MKT serves the key, BR-BIL-062).
    /// </summary>
    [Range(0, 365)]
    public int PayeeCoolingOffDays { get; set; } = 30;

    /// <summary>
    /// Policyholder type sent with <c>TaxCalculator.treatment</c> (REQ-BIL-079). A technical default: the consumed events
    /// carry no policyholder type, and the Greece treatment rows do not depend on it.
    /// </summary>
    public Market.Contracts.Spi.PolicyholderType TreatmentPolicyholderType { get; set; } = Market.Contracts.Spi.PolicyholderType.Consumer;

    /// <summary>Business basis code sent with <c>TaxCalculator.treatment</c>; technical default, see <see cref="TreatmentPolicyholderType"/>.</summary>
    [Required]
    public string TreatmentBusinessBasis { get; set; } = "ESTABLISHMENT";

    /// <summary>The configured zone.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>The configured default currency.</summary>
    public Currency Currency => Currency.FromCode(DefaultCurrency);
}
