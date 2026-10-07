using System.Globalization;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel;

/// <summary>
/// A calendar date without time or zone: an effective date, due date, loss date, birth date or period boundary.
/// Which zone turns it into an instant is a business rule (the legal entity's or jurisdiction's zone), so the
/// conversion always names the zone. JSON / text: ISO 8601 <c>yyyy-MM-dd</c> (contracts/events common <c>LocalDate</c>).
/// </summary>
[JsonConverter(typeof(BusinessDateJsonConverter))]
public readonly record struct BusinessDate(DateOnly Value) : IComparable<BusinessDate>
{
    private const string IsoFormat = "yyyy-MM-dd";

    /// <summary>Creates a date from its parts.</summary>
    public BusinessDate(int year, int month, int day)
        : this(new DateOnly(year, month, day))
    {
    }

    /// <summary>The earliest representable date.</summary>
    public static BusinessDate MinValue { get; } = new(DateOnly.MinValue);

    /// <summary>The latest representable date.</summary>
    public static BusinessDate MaxValue { get; } = new(DateOnly.MaxValue);

    /// <summary>Year part.</summary>
    public int Year => Value.Year;

    /// <summary>Month part.</summary>
    public int Month => Value.Month;

    /// <summary>Day part.</summary>
    public int Day => Value.Day;

    /// <summary>Calendar arithmetic (no business-day logic; that is the PLT calendar service).</summary>
    public BusinessDate AddDays(int days) => new(Value.AddDays(days));

    /// <summary>Calendar arithmetic; the day is clamped to the month end (31 Jan + 1 month = 28/29 Feb).</summary>
    public BusinessDate AddMonths(int months) => new(Value.AddMonths(months));

    /// <summary>Calendar arithmetic; 29 Feb + 1 year = 28 Feb.</summary>
    public BusinessDate AddYears(int years) => new(Value.AddYears(years));

    /// <summary>Number of days from <paramref name="earlier"/> to this date.</summary>
    public int DaysSince(BusinessDate earlier) => Value.DayNumber - earlier.Value.DayNumber;

    /// <summary>
    /// The UTC instant at which this date starts in <paramref name="zone"/>; e.g. 2027-01-01 in Europe/Athens is
    /// 2026-12-31T22:00:00Z (REQ-MKT-058).
    /// </summary>
    public Instant StartOfDayIn(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Midnight skipped by a daylight-saving jump: the day starts at the first valid minute.
            local = local.AddTicks(TimeSpan.TicksPerHour);
        }

        return Instant.FromUtcDateTime(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    /// <summary>Parses strict ISO 8601 <c>yyyy-MM-dd</c>.</summary>
    public static BusinessDate Parse(string text) =>
        TryParse(text, out var date) ? date : throw new FormatException($"'{text}' is not an ISO date (yyyy-MM-dd).");

    /// <summary>Tries to parse strict ISO 8601 <c>yyyy-MM-dd</c>.</summary>
    public static bool TryParse(string? text, out BusinessDate date)
    {
        if (text is { Length: 10 } && DateOnly.TryParseExact(text, IsoFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            date = new BusinessDate(value);
            return true;
        }

        date = default;
        return false;
    }

    /// <summary>ISO 8601 <c>yyyy-MM-dd</c>.</summary>
    public override string ToString() => Value.ToString(IsoFormat, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public int CompareTo(BusinessDate other) => Value.CompareTo(other.Value);

    /// <summary>Earlier of two dates.</summary>
    public static BusinessDate Min(BusinessDate left, BusinessDate right) => left <= right ? left : right;

    /// <summary>Later of two dates.</summary>
    public static BusinessDate Max(BusinessDate left, BusinessDate right) => left >= right ? left : right;

    /// <summary>Orders by date.</summary>
    public static bool operator <(BusinessDate left, BusinessDate right) => left.CompareTo(right) < 0;

    /// <summary>Orders by date.</summary>
    public static bool operator >(BusinessDate left, BusinessDate right) => left.CompareTo(right) > 0;

    /// <summary>Orders by date.</summary>
    public static bool operator <=(BusinessDate left, BusinessDate right) => left.CompareTo(right) <= 0;

    /// <summary>Orders by date.</summary>
    public static bool operator >=(BusinessDate left, BusinessDate right) => left.CompareTo(right) >= 0;
}
