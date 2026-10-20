using System.ComponentModel.DataAnnotations;

namespace CoreIns.Modules.Channels;

/// <summary>Configuration section <c>Channels</c>.</summary>
public sealed class ChannelsOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Channels";

    /// <summary>Time zone whose calendar days count the withdrawal window (the last day ends 23:59:59 there).</summary>
    [Required]
    public string TimeZone { get; set; } = "Europe/Athens";

    /// <summary>
    /// ILLUSTRATIVE (legalStatus ILLUSTRATIVE, D-SL6-06; PRD-05/PRD-17 not verified by the builder): days after the conclusion date
    /// in which the right can be exercised. To be replaced by the pack config <c>withdrawal.days</c> once MKT registers the key.
    /// </summary>
    [Range(1, 365)]
    public int WithdrawalDays { get; set; } = 14;

    /// <summary>Hours a confirmation token stays valid after the declaration.</summary>
    [Range(1, 24 * 14)]
    public int WithdrawalTokenHours { get; set; } = 48;

    /// <summary>
    /// Returns the confirmation token in the declare response (demo/ops/test stamps without a mail channel). The token also goes to the
    /// notification seam (<c>IWithdrawalNotifier</c>); production sets this to false once a real e-mail channel replaces the logging default.
    /// </summary>
    public bool ReturnWithdrawalToken { get; set; } = true;

    /// <summary>Seconds between sweeps that re-submit CONFIRMED withdrawals to POL (worker role).</summary>
    [Range(1, 86400)]
    public int SweepIntervalSeconds { get; set; } = 300;

    /// <summary>Seconds a request stays CONFIRMED before the sweeper takes it (the confirm call itself may still be finishing).</summary>
    [Range(0, 86400)]
    public int SweepGraceSeconds { get; set; } = 60;

    /// <summary>The configured zone.</summary>
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}
