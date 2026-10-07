namespace CoreIns.SharedKernel;

/// <summary>
/// One version of a bitemporal value (contract §3.2.1, ADR §2 rule 4): <see cref="Value"/> holds in business (valid)
/// time <see cref="Valid"/> = <c>[validFrom, validTo)</c> and was the system's belief during record (transaction) time
/// <see cref="Recorded"/> = <c>[recordedFrom, recordedTo)</c>. Corrections never edit a version: they close its record
/// window (<see cref="Supersede"/>) and add new versions. Time-travel reads use <c>validAt</c> and <c>knownAt</c>
/// (D-API-02/D-API-08).
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="Valid">Business validity <c>[validFrom, validTo)</c>.</param>
/// <param name="Recorded">Record time <c>[recordedFrom, recordedTo)</c>; open while this is the current belief.</param>
public sealed record Bitemporal<T>(T Value, InstantRange Valid, InstantRange Recorded)
{
    /// <summary>Valid-from (inclusive).</summary>
    public Instant ValidFrom => Valid.Start;

    /// <summary>Valid-to (exclusive); null when open-ended.</summary>
    public Instant? ValidTo => Valid.End;

    /// <summary>Recorded-from (inclusive).</summary>
    public Instant RecordedFrom => Recorded.Start;

    /// <summary>Recorded-to (exclusive); null while this version is current.</summary>
    public Instant? RecordedTo => Recorded.End;

    /// <summary>True while this version has not been superseded in record time.</summary>
    public bool IsCurrent => Recorded.IsOpen;

    /// <summary>True when the version is valid at <paramref name="validAt"/> and was known at <paramref name="knownAt"/>.</summary>
    public bool IsVisibleAt(Instant validAt, Instant knownAt) => Valid.Contains(validAt) && Recorded.Contains(knownAt);

    /// <summary>This version with its record window closed at <paramref name="recordedAt"/> (the belief ends; the row is kept).</summary>
    public Bitemporal<T> Supersede(Instant recordedAt)
    {
        if (!IsCurrent)
        {
            throw new InvalidOperationException($"The version recorded {Recorded} is already superseded.");
        }

        return this with { Recorded = Recorded.CloseAt(recordedAt) };
    }
}

/// <summary>Helpers over sets of <see cref="Bitemporal{T}"/> versions.</summary>
public static class Bitemporal
{
    /// <summary>Picks the version visible at (<paramref name="validAt"/>, <paramref name="knownAt"/>); null when none.</summary>
    public static Bitemporal<T>? VisibleAt<T>(IEnumerable<Bitemporal<T>> versions, Instant validAt, Instant knownAt)
    {
        ArgumentNullException.ThrowIfNull(versions);
        Bitemporal<T>? found = null;
        foreach (var version in versions.Where(v => v.IsVisibleAt(validAt, knownAt)))
        {
            if (found is not null)
            {
                throw new InvalidOperationException(
                    $"Two versions are visible at validAt={validAt}, knownAt={knownAt}: {found.Valid}/{found.Recorded} and {version.Valid}/{version.Recorded}.");
            }

            found = version;
        }

        return found;
    }
}
