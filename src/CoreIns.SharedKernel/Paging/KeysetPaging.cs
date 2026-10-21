using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CoreIns.SharedKernel.Paging;

/// <summary>
/// The platform keyset-paging standard (NX-HD-12): a capped page size, a stable total order, and an opaque cursor that carries the
/// sort key of the last row served (never an offset, which skips or repeats rows while the list changes). The cursor is base64url of
/// a JSON string array, one entry per sort-key part; a malformed cursor or a limit outside 1..<see cref="MaxPageSize"/> is refused by
/// the caller (fail closed), not silently reset. Callers query <c>take + 1</c> rows in a total order and call <see cref="Slice{T}"/>.
/// </summary>
public static class KeysetPaging
{
    /// <summary>The largest page any list endpoint serves.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The page size when the caller gives none.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Strict: the limit is the default when absent, else within 1..<see cref="MaxPageSize"/>; false when out of range.</summary>
    public static bool TryTake(int? limit, int defaultSize, out int take)
    {
        take = limit ?? defaultSize;
        return take is >= 1 and <= MaxPageSize;
    }

    /// <summary>Lenient: clamps into 1..<see cref="MaxPageSize"/> (for endpoints whose contract never refused an out-of-range limit).</summary>
    public static int Clamp(int? limit, int defaultSize = DefaultPageSize) => Math.Clamp(limit ?? defaultSize, 1, MaxPageSize);

    /// <summary>The opaque cursor for the given sort-key parts (culture-invariant text).</summary>
    public static string Encode(params string[] parts) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(parts)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Decodes a cursor of exactly <paramref name="partCount"/> parts. A null or empty cursor is "first page" and returns true with
    /// null parts; a malformed one returns false.
    /// </summary>
    public static bool TryDecode(string? cursor, int partCount, out string[]? parts)
    {
        parts = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var decoded = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
            if (decoded is null || decoded.Length != partCount || decoded.Any(p => p is null))
            {
                return false;
            }

            parts = decoded;
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Cursor part for a long (timestamps as unix ticks, counters).</summary>
    public static string Part(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Cursor part for a uuid.</summary>
    public static string Part(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Parses a cursor part written by <see cref="Part(long)"/>.</summary>
    public static bool TryLong(string part, out long value) => long.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Cuts the <c>take + 1</c> rows read into the page and the next cursor (built from the page's last row), or null on the last page.
    /// </summary>
    public static (List<T> Page, string? NextCursor) Slice<T>(IReadOnlyList<T> rowsTakePlusOne, int take, Func<T, string> cursorOf)
    {
        var page = rowsTakePlusOne.Take(take).ToList();
        return (page, rowsTakePlusOne.Count > take ? cursorOf(page[^1]) : null);
    }

    /// <summary>
    /// The first <paramref name="count"/> ids strictly after <paramref name="after"/>, in ascending order. For tables keyed by a strongly
    /// typed id, which the SQL provider cannot compare: read the (filtered) ids, pick the page here, then load those rows.
    /// TODO(hardening): compare the id in SQL once the typed ids translate.
    /// </summary>
    public static List<Guid> IdsAfter(IEnumerable<Guid> ids, Guid? after, int count) =>
        [.. ids.Where(i => after is null || i.CompareTo(after.Value) > 0).Order().Take(count)];
}
