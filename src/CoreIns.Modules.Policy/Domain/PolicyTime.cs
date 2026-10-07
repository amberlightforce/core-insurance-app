using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Domain;

/// <summary>
/// Time rules at the edge (REQ-POL-041): effective instants are timestamps; valid periods are half-open [from, to);
/// conversion to local dates uses the legal entity's zone only where a date is required (charge periods, booking date).
/// </summary>
internal static class PolicyTime
{
    /// <summary>End (exclusive) of an annual term starting at <paramref name="start"/>: the same local time one year later.</summary>
    public static Instant AnnualEnd(Instant start, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(start.ToUtcDateTime(), zone);
        var next = DateTime.SpecifyKind(local.AddYears(1), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(next))
        {
            next = next.AddTicks(TimeSpan.TicksPerHour);
        }

        return Instant.FromUtcDateTime(TimeZoneInfo.ConvertTimeToUtc(next, zone));
    }

    /// <summary>
    /// The instant a date-form <c>validAt</c> stands for (D-SLC-13): the end of that business day in the legal entity's
    /// zone, i.e. the last microsecond before the next day starts ("as of date D" = "at close of business on D").
    /// </summary>
    public static Instant EndOf(BusinessDate date, TimeZoneInfo zone) =>
        new BusinessDate(date.Value.AddDays(1)).StartOfDayIn(zone).Plus(TimeSpan.FromTicks(-TimeSpan.TicksPerMillisecond / 1000));

    /// <summary>The term period as local dates, half-open (the dates the charge deltas carry).</summary>
    public static DateRange Dates(Instant from, Instant to, TimeZoneInfo zone) => DateRange.Of(from.ToBusinessDate(zone), to.ToBusinessDate(zone));
}
