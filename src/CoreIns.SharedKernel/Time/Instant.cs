using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel;

/// <summary>
/// A point on the UTC time line with microsecond precision (the precision of PostgreSQL <c>timestamptz</c>, so a value
/// survives a database round trip unchanged). Finer ticks are truncated. The current instant comes only from the
/// platform time service (<c>IClock</c>, contract §3.9.13), never from the system clock.
/// JSON / text: RFC 3339 in UTC with a <c>Z</c> suffix (contracts/events common <c>Instant</c>).
/// </summary>
[JsonConverter(typeof(InstantJsonConverter))]
public readonly partial record struct Instant : IComparable<Instant>
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMicrosecond;

    private readonly long _utcTicks;

    private Instant(long utcTicks) => _utcTicks = utcTicks - (utcTicks % TicksPerMicrosecond);

    /// <summary>The earliest representable instant.</summary>
    public static Instant MinValue { get; } = new(DateTime.MinValue.Ticks);

    /// <summary>The latest representable instant (microsecond precision).</summary>
    public static Instant MaxValue { get; } = new(DateTime.MaxValue.Ticks);

    /// <summary>Unix epoch, 1970-01-01T00:00:00Z.</summary>
    public static Instant UnixEpoch { get; } = new(DateTime.UnixEpoch.Ticks);

    /// <summary>Converts any offset to UTC, truncating to microseconds.</summary>
    public static Instant FromDateTimeOffset(DateTimeOffset value) => new(value.UtcTicks);

    /// <summary>Converts a UTC <see cref="DateTime"/>; any other kind is rejected (no guessing of time zones).</summary>
    public static Instant FromUtcDateTime(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? new Instant(value.Ticks)
            : throw new ArgumentException("Only DateTimeKind.Utc can be converted to an Instant.", nameof(value));

    /// <summary>Creates an instant from UTC calendar fields.</summary>
    public static Instant FromUtc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks);

    /// <summary>The instant as a <see cref="DateTimeOffset"/> with offset zero.</summary>
    public DateTimeOffset ToDateTimeOffset() => new(_utcTicks, TimeSpan.Zero);

    /// <summary>The instant as a UTC <see cref="DateTime"/>.</summary>
    public DateTime ToUtcDateTime() => new(_utcTicks, DateTimeKind.Utc);

    /// <summary>The business date of this instant in a time zone (e.g. Europe/Athens for a Greek legal entity).</summary>
    public BusinessDate ToBusinessDate(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return new BusinessDate(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(ToUtcDateTime(), zone)));
    }

    /// <summary>The UTC date of this instant.</summary>
    public BusinessDate UtcDate => new(DateOnly.FromDateTime(ToUtcDateTime()));

    /// <summary>Adds a duration.</summary>
    public Instant Plus(TimeSpan duration) => new(checked(_utcTicks + duration.Ticks));

    /// <summary>Subtracts a duration.</summary>
    public Instant Minus(TimeSpan duration) => new(checked(_utcTicks - duration.Ticks));

    /// <summary>The duration from <paramref name="earlier"/> to this instant.</summary>
    public TimeSpan Since(Instant earlier) => TimeSpan.FromTicks(_utcTicks - earlier._utcTicks);

    /// <summary>Parses strict RFC 3339 UTC text (<c>yyyy-MM-ddTHH:mm:ss[.fraction]Z</c>); fractions beyond microseconds are truncated.</summary>
    public static Instant Parse(string text) =>
        TryParse(text, out var instant) ? instant : throw new FormatException($"'{text}' is not an RFC 3339 UTC instant (…Z).");

    /// <summary>Tries to parse strict RFC 3339 UTC text.</summary>
    public static bool TryParse(string? text, out Instant instant)
    {
        instant = default;
        if (text is null)
        {
            return false;
        }

        var match = Rfc3339Utc().Match(text);
        if (!match.Success)
        {
            return false;
        }

        if (!DateTime.TryParseExact(match.Groups["seconds"].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var whole))
        {
            return false;
        }

        long fractionTicks = 0;
        var fraction = match.Groups["fraction"].Value;
        if (fraction.Length > 0)
        {
            var micro = fraction.Length >= 6 ? fraction[..6] : fraction.PadRight(6, '0');
            fractionTicks = long.Parse(micro, NumberStyles.None, CultureInfo.InvariantCulture) * TicksPerMicrosecond;
        }

        instant = new Instant(whole.Ticks + fractionTicks);
        return true;
    }

    /// <summary>RFC 3339 UTC text with up to six fraction digits, trailing zeros omitted (e.g. <c>2026-10-07T08:00:00.5Z</c>).</summary>
    public override string ToString() =>
        ToUtcDateTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public int CompareTo(Instant other) => _utcTicks.CompareTo(other._utcTicks);

    /// <summary>Earlier of two instants.</summary>
    public static Instant Min(Instant left, Instant right) => left <= right ? left : right;

    /// <summary>Later of two instants.</summary>
    public static Instant Max(Instant left, Instant right) => left >= right ? left : right;

    /// <summary>Adds a duration.</summary>
    public static Instant operator +(Instant left, TimeSpan right) => left.Plus(right);

    /// <summary>Subtracts a duration.</summary>
    public static Instant operator -(Instant left, TimeSpan right) => left.Minus(right);

    /// <summary>The duration between two instants.</summary>
    public static TimeSpan operator -(Instant left, Instant right) => left.Since(right);

    /// <summary>Orders on the time line.</summary>
    public static bool operator <(Instant left, Instant right) => left.CompareTo(right) < 0;

    /// <summary>Orders on the time line.</summary>
    public static bool operator >(Instant left, Instant right) => left.CompareTo(right) > 0;

    /// <summary>Orders on the time line.</summary>
    public static bool operator <=(Instant left, Instant right) => left.CompareTo(right) <= 0;

    /// <summary>Orders on the time line.</summary>
    public static bool operator >=(Instant left, Instant right) => left.CompareTo(right) >= 0;

    [GeneratedRegex("^(?<seconds>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\\.(?<fraction>[0-9]{1,9}))?Z\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339Utc();
}
