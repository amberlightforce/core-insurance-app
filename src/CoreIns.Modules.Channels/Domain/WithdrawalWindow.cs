using CoreIns.SharedKernel;

namespace CoreIns.Modules.Channels.Domain;

/// <summary>Withdrawal request states (<c>chn.withdrawal_request.status</c>).</summary>
internal static class WithdrawalStatuses
{
    public const string Declared = "DECLARED";
    public const string Confirmed = "CONFIRMED";
    public const string Submitted = "SUBMITTED";
    public const string Processed = "PROCESSED";
    public const string Rejected = "REJECTED";
    public const string Declined = "DECLINED";

    public static readonly string[] All = [Declared, Confirmed, Submitted, Processed, Rejected, Declined];

    public const string RightExpired = "CHN-ERR-RIGHT-EXPIRED";
}

/// <summary>
/// The withdrawal window (D-SL6-06): the conclusion day is day 0; the last day is day <c>days</c>, counted in the legal entity's
/// zone, and it ends at 23:59:59 local time. DST-safe: the window is computed from local calendar days, never by adding 24 h.
/// </summary>
internal static class WithdrawalWindow
{
    /// <summary>First instant after the window (local midnight starting the day after the last day).</summary>
    public static Instant EndExclusive(Instant concludedAt, int days, TimeZoneInfo zone) =>
        concludedAt.ToBusinessDate(zone).AddDays(days + 1).StartOfDayIn(zone);

    /// <summary>The last second of the last day (shown to the customer).</summary>
    public static Instant Deadline(Instant concludedAt, int days, TimeZoneInfo zone) =>
        EndExclusive(concludedAt, days, zone).Minus(TimeSpan.FromSeconds(1));

    /// <summary>Whether <paramref name="at"/> is inside the window.</summary>
    public static bool IsOpen(Instant concludedAt, Instant at, int days, TimeZoneInfo zone) =>
        at < EndExclusive(concludedAt, days, zone);
}
