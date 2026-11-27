using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Market.Domain;

/// <summary>
/// Approved configuration changes (BO-MKT-01) are recorded as an overlay: a registered pack version <c>cfg-overlay.&lt;country&gt;</c> that
/// holds every value changed so far for one country layer. The overlay is named in the state manifest like any pack version, so a change
/// gives a new state hash, the history stays append-only and an older hash still resolves the values it saw (REQ-MKT-048). Where an overlay
/// value overlaps a pack value for the same key, the overlay wins for the overlapping dates only; the pack value stays outside them.
/// </summary>
internal static class ConfigOverlay
{
    public const string PackPrefix = "cfg-overlay.";

    public static string PackIdFor(string country) => PackPrefix + country;

    public static bool IsOverlay(string packId) => packId.StartsWith(PackPrefix, StringComparison.Ordinal);

    /// <summary>The pieces of the range <paramref name="from"/>..<paramref name="to"/> (open ends are null) left after removing <paramref name="cutFrom"/>..<paramref name="cutTo"/>.</summary>
    public static IEnumerable<(BusinessDate? From, BusinessDate? To)> Remainder(BusinessDate? from, BusinessDate? to, BusinessDate? cutFrom, BusinessDate? cutTo)
    {
        if (cutFrom is { } cf && (from is null || from.Value < cf))
        {
            yield return (from, to is { } t && t < cf ? t : cf);
        }

        if (cutTo is { } ct && (to is null || to.Value > ct))
        {
            yield return (from is { } f && f > ct ? f : ct, to);
        }
    }

    /// <summary>
    /// The overlay values after applying <paramref name="incoming"/> to <paramref name="existing"/>: a later value replaces an earlier one for the same key
    /// on the dates they share (the earlier one keeps the dates outside the overlap).
    /// </summary>
    public static List<PackConfigValue> Apply(IEnumerable<PackConfigValue> existing, IEnumerable<PackConfigValue> incoming)
    {
        var result = existing.ToList();
        foreach (var value in incoming)
        {
            var range = new DateRange(value.ValidFrom ?? BusinessDate.MinValue, value.ValidTo);
            var next = new List<PackConfigValue>();
            foreach (var old in result)
            {
                if (old.Key != value.Key || !new DateRange(old.ValidFrom ?? BusinessDate.MinValue, old.ValidTo).Overlaps(range))
                {
                    next.Add(old);
                    continue;
                }

                next.AddRange(Remainder(old.ValidFrom, old.ValidTo, value.ValidFrom, value.ValidTo)
                    .Select(piece => old with { ValidFrom = piece.From, ValidTo = piece.To }));
            }

            next.Add(value);
            result = next;
        }

        return result;
    }
}
