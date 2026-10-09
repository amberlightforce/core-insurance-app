using System.ComponentModel.DataAnnotations;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy;

/// <summary>
/// Configuration section <c>Policy</c>. These are stamp settings, not regulatory values: the legal entity's time zone
/// (zone conversion happens only at the edge, REQ-POL-041), the term currency used when a submission names none, and the
/// quote validity (<c>pol.quote.validity_days</c>, PRD-05 §10.2 default 30) until MKT's configuration resolver serves it.
/// </summary>
internal sealed class PolicyOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Policy";

    /// <summary>IANA time zone of the stamp's legal entity (e.g. Europe/Athens).</summary>
    [Required]
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>ISO 4217 currency of terms when the submission names none.</summary>
    [Required]
    [RegularExpression("^[A-Z]{3}$")]
    public string DefaultCurrency { get; set; } = string.Empty;

    /// <summary>Days a quoted version stays bindable (<c>pol.quote.validity_days</c>).</summary>
    [Range(1, 366)]
    public int QuoteValidityDays { get; set; }

    /// <summary>
    /// Quote without <c>pfc.PolicyDraft.validate</c> when PFC does not provide it. Off by default (fail closed); only for
    /// Development and tests, and every such quote logs a warning.
    /// </summary>
    public bool AllowMissingDraftValidation { get; set; }

    /// <summary>
    /// Seconds a command waits for the policy's write lock (D-SL3-03) before it fails with <c>POL-ERR-STALE</c> (409). Writers of
    /// one policy are serialised; the loser waits this long and then reports "changed meanwhile" instead of a server error.
    /// </summary>
    [Range(1, 120)]
    public int LockWaitSeconds { get; set; } = 5;

    /// <summary>The policy write-lock wait.</summary>
    public TimeSpan LockWait => TimeSpan.FromSeconds(LockWaitSeconds);

    /// <summary>The configured zone.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>The configured default currency.</summary>
    public Currency Currency => Currency.FromCode(DefaultCurrency);
}
