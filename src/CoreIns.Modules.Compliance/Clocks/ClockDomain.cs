using System.Globalization;
using System.Text.RegularExpressions;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Compliance.Clocks;

/// <summary>Legal status tag of a regulatory value (PRD-11 rule: no regulatory value without a tag).</summary>
internal static class ClockLegalStatus
{
    public const string Verified = "VERIFIED";
    public const string SecondarySource = "SECONDARY_SOURCE";
    public const string Illustrative = "ILLUSTRATIVE";
}

/// <summary>An ISO 8601 calendar period (<c>PnYnMnWnD</c>); the length of a clock.</summary>
internal readonly record struct IsoPeriod(int Years, int Months, int Days)
{
    private static readonly Regex Pattern = new(@"^P(?:(?<y>\d+)Y)?(?:(?<m>\d+)M)?(?:(?<w>\d+)W)?(?:(?<d>\d+)D)?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    /// <summary>Parses <c>PnYnMnWnD</c>; time parts are not supported (a clock counts calendar days).</summary>
    public static bool TryParse(string? text, out IsoPeriod period)
    {
        period = default;
        if (string.IsNullOrEmpty(text) || text == "P")
        {
            return false;
        }

        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return false;
        }

        static int Part(Group g) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
        try
        {
            period = new IsoPeriod(Part(match.Groups["y"]), Part(match.Groups["m"]), checked((Part(match.Groups["w"]) * 7) + Part(match.Groups["d"])));
        }
        catch (OverflowException)
        {
            return false;
        }

        return period.Years <= 100 && period.Months <= 1200 && period.Days <= 36500;
    }

    public static IsoPeriod Parse(string text) =>
        TryParse(text, out var period) ? period : throw new FormatException($"'{text}' is not an ISO 8601 period (PnYnMnWnD).");

    /// <summary>Calendar addition: years and months first (day clamped to the month end), then days.</summary>
    public BusinessDate AddTo(BusinessDate date) => date.AddYears(Years).AddMonths(Months).AddDays(Days);
}

/// <summary>One clock definition: what runs, how long, and where the value comes from (REQ-CMP-092..094).</summary>
internal sealed record ClockDefinition(
    string Code,
    string Name,
    StatutoryClockKind Kind,
    string Duration,
    string Jurisdiction,
    string LegalStatus,
    string Source,
    bool Extendable = false,
    string? WarnBefore = null)
{
    public IsoPeriod Period => IsoPeriod.Parse(Duration);

    public string KindCode => Kind == StatutoryClockKind.WaitingPeriod ? "WAITING_PERIOD" : "DEADLINE";
}

/// <summary>
/// The seeded definitions (SL6-CMP-CLOCK, D-SL6-04). Regulatory values carry a legal status tag and a source; where the
/// number is not verified it is ILLUSTRATIVE.
/// </summary>
internal static class ClockDefinitions
{
    public const string BilNonpayNotice = "BIL_NONPAY_NOTICE";
    public const string DsarResponse = "CMP_DSAR_RESPONSE";
    public const string ClmFsCounterpartyReply = "CLM_FS_COUNTERPARTY_REPLY";
    public const string ClmOpenClaimResponse = "CLM_OPEN_CLAIM_RESPONSE";

    public static IReadOnlyList<ClockDefinition> All { get; } =
    [
        // D-SL6-04: one calendar month from the notification date, end of day Europe/Athens.
        new(BilNonpayNotice, "Non-payment notice waiting period", StatutoryClockKind.WaitingPeriod, "P1M", "GR",
            ClockLegalStatus.SecondarySource, "Law 2496/1997 Art 6 par 2 (PRD-06 R-60, K-01)"),

        // GDPR Art 12(3): one month; extendable by two further months (Art 12(3)). The 7-day warning is illustrative.
        new(DsarResponse, "Data subject request deadline", StatutoryClockKind.Deadline, "P1M", "GR",
            ClockLegalStatus.Verified, "GDPR Art 12(3) (PRD-18 section 9.2)", Extendable: true, WarnBefore: "P7D"),

        // Placeholder: the 30 days are not from a source.
        new(ClmFsCounterpartyReply, "FS counterparty reply", StatutoryClockKind.Deadline, "P30D", "GR",
            ClockLegalStatus.Illustrative, "PRD-08 placeholder, not verified", WarnBefore: "P5D"),

        // Placeholder for the claim handling deadline an imported open claim keeps (CLM-010, CMP-009): the number is not sourced.
        new(ClmOpenClaimResponse, "Open claim handling deadline", StatutoryClockKind.Deadline, "P30D", "GR",
            ClockLegalStatus.Illustrative, "PRD-07 placeholder, not verified", WarnBefore: "P5D"),
    ];

    public static ClockDefinition? Find(string code) => All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));
}

/// <summary>Deadline arithmetic. Always in the legal entity's zone, never UTC (PITFALLS time handling).</summary>
internal static class ClockMath
{
    private static readonly TimeSpan OneMicrosecond = TimeSpan.FromTicks(TimeSpan.TicksPerMicrosecond);

    /// <summary>
    /// The due instant: the local date of <paramref name="start"/> plus the period (calendar months with clamp, so 31 Jan
    /// plus one month is 28/29 Feb), due at the end of that local day. The instant is the last microsecond of the day.
    /// </summary>
    public static Instant Deadline(Instant start, IsoPeriod period, TimeZoneInfo zone) => EndOfDay(period.AddTo(start.ToBusinessDate(zone)), zone);

    /// <summary>The last microsecond of a local day (DST days have 23 or 25 hours).</summary>
    public static Instant EndOfDay(BusinessDate date, TimeZoneInfo zone) => date.AddDays(1).StartOfDayIn(zone).Minus(OneMicrosecond);

    /// <summary>The deadline after an extension: the current due date plus the period, again at the end of the day.</summary>
    public static Instant Extend(Instant deadline, IsoPeriod period, TimeZoneInfo zone) => Deadline(deadline, period, zone);

    /// <summary>The deadline after a pause: the clock did not run while paused, so the due instant moves by the paused time.</summary>
    public static Instant Shift(Instant deadline, Instant pausedAt, Instant resumedAt) => deadline.Plus(resumedAt > pausedAt ? resumedAt - pausedAt : TimeSpan.Zero);

    /// <summary>The warning instant, <paramref name="warnBefore"/> calendar days or months before the deadline's local day.</summary>
    public static Instant? WarnAt(Instant deadline, string? warnBefore, TimeZoneInfo zone)
    {
        if (warnBefore is null || !IsoPeriod.TryParse(warnBefore, out var before))
        {
            return null;
        }

        var date = deadline.ToBusinessDate(zone).AddYears(-before.Years).AddMonths(-before.Months).AddDays(-before.Days);
        return date.StartOfDayIn(zone);
    }
}

/// <summary>Status codes as stored (upper case) and the mapping to the contract's state model.</summary>
internal static class ClockStatus
{
    public const string Running = "RUNNING";
    public const string Warned = "WARNED";
    public const string Paused = "PAUSED";
    public const string Met = "MET";
    public const string Breached = "BREACHED";
    public const string Elapsed = "ELAPSED";
    public const string Cancelled = "CANCELLED";

    public static string Code(StatutoryClockState state) => state.ToString().ToUpperInvariant();

    public static StatutoryClockState Parse(string code) => Enum.Parse<StatutoryClockState>(code, ignoreCase: true);

    /// <summary>True for states that still count down.</summary>
    public static bool IsLive(string status) => status is Running or Warned;
}
