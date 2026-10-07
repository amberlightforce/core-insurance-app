using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel;

/// <summary>
/// A half-open business-date period <c>[Start, End)</c> (contract §3.2.1, D5 / XMR-FR-150): <see cref="Start"/> is
/// included, <see cref="End"/> is excluded, and a null <see cref="End"/> means open-ended. A period is never empty
/// (<c>Start &lt; End</c>), so two periods that merely touch (<c>a.End == b.Start</c>) do not overlap — the same rule
/// as a PostgreSQL <c>daterange '[)'</c> exclusion constraint.
/// JSON: <c>{"from": "yyyy-MM-dd", "to": "yyyy-MM-dd" | null}</c> (contracts/events common <c>DatePeriod</c>).
/// </summary>
[JsonConverter(typeof(DateRangeJsonConverter))]
public readonly record struct DateRange
{
    /// <summary>Creates <c>[start, end)</c>; <paramref name="end"/> null = open-ended. Throws when <c>end &lt;= start</c>.</summary>
    public DateRange(BusinessDate start, BusinessDate? end)
    {
        if (end is { } e && e <= start)
        {
            throw new ArgumentException($"A period must end after it starts: [{start}, {e}) is empty or inverted.", nameof(end));
        }

        Start = start;
        End = end;
    }

    /// <summary>First day in the period (inclusive).</summary>
    public BusinessDate Start { get; }

    /// <summary>First day after the period (exclusive); null when open-ended.</summary>
    public BusinessDate? End { get; }

    /// <summary>True when the period has no end.</summary>
    public bool IsOpen => End is null;

    /// <summary><c>[start, end)</c>.</summary>
    public static DateRange Of(BusinessDate start, BusinessDate end) => new(start, end);

    /// <summary><c>[start, ∞)</c>.</summary>
    public static DateRange Open(BusinessDate start) => new(start, null);

    /// <summary>Number of days in a closed period; null when open-ended.</summary>
    public int? Days => End is { } end ? end.DaysSince(Start) : null;

    /// <summary>True when <paramref name="date"/> lies in <c>[Start, End)</c>.</summary>
    public bool Contains(BusinessDate date) => date >= Start && (End is null || date < End.Value);

    /// <summary>True when <paramref name="other"/> lies entirely within this period.</summary>
    public bool Contains(DateRange other) =>
        other.Start >= Start && (End is null || (other.End is { } otherEnd && otherEnd <= End.Value));

    /// <summary>True when the periods share at least one day.</summary>
    public bool Overlaps(DateRange other) =>
        (End is null || other.Start < End.Value) && (other.End is null || Start < other.End.Value);

    /// <summary>The common part of two periods, or null when they do not overlap.</summary>
    public DateRange? Intersect(DateRange other)
    {
        if (!Overlaps(other))
        {
            return null;
        }

        var start = BusinessDate.Max(Start, other.Start);
        BusinessDate? end = (End, other.End) switch
        {
            (null, null) => null,
            (null, { } b) => b,
            ({ } a, null) => a,
            ({ } a, { } b) => BusinessDate.Min(a, b),
        };
        return new DateRange(start, end);
    }

    /// <summary>True when this period ends exactly where <paramref name="other"/> starts.</summary>
    public bool Meets(DateRange other) => End is { } end && end == other.Start;

    /// <summary>Splits at <paramref name="at"/> into <c>[Start, at)</c> and <c>[at, End)</c>; <paramref name="at"/> must lie strictly inside.</summary>
    public (DateRange Before, DateRange After) SplitAt(BusinessDate at)
    {
        if (at <= Start || (End is { } end && at >= end))
        {
            throw new ArgumentOutOfRangeException(nameof(at), at, $"Split point must lie strictly inside {this}.");
        }

        return (new DateRange(Start, at), new DateRange(at, End));
    }

    /// <summary>Interval notation, e.g. <c>[2026-01-01, 2027-01-01)</c> or <c>[2026-01-01, ∞)</c>.</summary>
    public override string ToString() => $"[{Start}, {(End is { } end ? end.ToString() : "∞")})";
}

/// <summary>
/// A half-open time window <c>[Start, End)</c> on the UTC time line (record time, effective time); a null
/// <see cref="End"/> means open-ended. Never empty. JSON: <c>{"from": instant, "to": instant | null}</c>
/// (contracts/events common <c>TimeWindow</c>).
/// </summary>
[JsonConverter(typeof(InstantRangeJsonConverter))]
public readonly record struct InstantRange
{
    /// <summary>Creates <c>[start, end)</c>; <paramref name="end"/> null = open-ended. Throws when <c>end &lt;= start</c>.</summary>
    public InstantRange(Instant start, Instant? end)
    {
        if (end is { } e && e <= start)
        {
            throw new ArgumentException($"A window must end after it starts: [{start}, {e}) is empty or inverted.", nameof(end));
        }

        Start = start;
        End = end;
    }

    /// <summary>Start (inclusive).</summary>
    public Instant Start { get; }

    /// <summary>End (exclusive); null when open-ended.</summary>
    public Instant? End { get; }

    /// <summary>True when the window has no end.</summary>
    public bool IsOpen => End is null;

    /// <summary><c>[start, end)</c>.</summary>
    public static InstantRange Of(Instant start, Instant end) => new(start, end);

    /// <summary><c>[start, ∞)</c>.</summary>
    public static InstantRange Open(Instant start) => new(start, null);

    /// <summary>True when <paramref name="instant"/> lies in <c>[Start, End)</c>.</summary>
    public bool Contains(Instant instant) => instant >= Start && (End is null || instant < End.Value);

    /// <summary>True when <paramref name="other"/> lies entirely within this window.</summary>
    public bool Contains(InstantRange other) =>
        other.Start >= Start && (End is null || (other.End is { } otherEnd && otherEnd <= End.Value));

    /// <summary>True when the windows share at least one instant.</summary>
    public bool Overlaps(InstantRange other) =>
        (End is null || other.Start < End.Value) && (other.End is null || Start < other.End.Value);

    /// <summary>The common part of two windows, or null when they do not overlap.</summary>
    public InstantRange? Intersect(InstantRange other)
    {
        if (!Overlaps(other))
        {
            return null;
        }

        var start = Instant.Max(Start, other.Start);
        Instant? end = (End, other.End) switch
        {
            (null, null) => null,
            (null, { } b) => b,
            ({ } a, null) => a,
            ({ } a, { } b) => Instant.Min(a, b),
        };
        return new InstantRange(start, end);
    }

    /// <summary>The same window closed at <paramref name="end"/> (e.g. when a record version is superseded).</summary>
    public InstantRange CloseAt(Instant end) => new(Start, end);

    /// <summary>Interval notation.</summary>
    public override string ToString() => $"[{Start}, {(End is { } end ? end.ToString() : "∞")})";
}
