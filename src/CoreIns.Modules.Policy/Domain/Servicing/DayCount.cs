using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Domain.Servicing;

/// <summary>Whole Europe/Athens calendar-date arithmetic, half-open: days(from, to) = AthensDate(to) - AthensDate(from) (D-SL3-04).</summary>
internal static class DayCount
{
    public static int Days(Instant from, Instant to, TimeZoneInfo zone) =>
        to.ToBusinessDate(zone).Value.DayNumber - from.ToBusinessDate(zone).Value.DayNumber;

    public static DateOnly Date(Instant instant, TimeZoneInfo zone) => instant.ToBusinessDate(zone).Value;
}

/// <summary>An exact proration fraction (days ÷ basis); no floating point anywhere.</summary>
internal readonly record struct ProrationFraction(int Numerator, int Denominator);

/// <summary>
/// Port for the one shared proration (REQ-RAT-004), implemented by SL3-RAT-PRORATE. The engine asks only for the
/// fraction; amounts are computed and rounded here with exact decimal arithmetic.
/// </summary>
internal interface IProration
{
    /// <summary>The fraction of an annual rate for <paramref name="days"/> days of a term of <paramref name="termDays"/> days.</summary>
    ProrationFraction Fraction(DayCountConvention convention, int days, int termDays);
}

/// <summary>Reference implementation for tests only: TERM_RATIO = days/termDays; ACT/365F = days/365, full term = 1.</summary>
internal sealed class ReferenceProration : IProration
{
    public ProrationFraction Fraction(DayCountConvention convention, int days, int termDays)
    {
        if (days < 0 || termDays <= 0 || days > termDays)
        {
            throw new ArgumentOutOfRangeException(nameof(days), "Days must be within the term.");
        }

        return convention switch
        {
            DayCountConvention.TermRatio => new ProrationFraction(days, termDays),
            DayCountConvention.Act365F => days == termDays ? new ProrationFraction(1, 1) : new ProrationFraction(days, 365),
            _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, "Unknown convention."),
        };
    }
}
