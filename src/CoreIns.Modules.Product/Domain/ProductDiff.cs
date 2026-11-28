using System.Text.Json.Nodes;
using CoreIns.Modules.Product.Contracts.Api;

namespace CoreIns.Modules.Product.Domain;

/// <summary>
/// Pure item-level diff of two product definitions as JSON (BO-PFC-03, REQ-PFC-200, D-USR-02). Objects compare per property; arrays whose items are
/// all objects with a unique string <c>code</c> (elements, coverages, chargeTypes, ...) compare per code, so an added coverage is one entry and a
/// changed limit is one entry under that coverage; every other array or scalar is atomic. Uses the same coded-array rule as <see cref="ThreeWayMerge"/>.
/// </summary>
internal static class ProductDiff
{
    public static IReadOnlyList<ProductDiffGroup> Compute(JsonNode from, JsonNode to)
    {
        var entries = new List<ProductDiffEntry>();
        Walk(from, to, string.Empty, null, entries);
        return entries
            .GroupBy(e => AreaOf(e.Path))
            .Select(g => new ProductDiffGroup { Area = g.Key, Entries = [.. g] })
            .ToList();
    }

    /// <summary>The top-level part of the definition an entry belongs to (<c>/elements[TERM_RATIO]/x</c> gives <c>elements</c>).</summary>
    internal static string AreaOf(string path)
    {
        var rest = path.TrimStart('/');
        var end = rest.IndexOfAny(['/', '[']);
        return end < 0 ? rest : rest[..end];
    }

    private static void Walk(JsonNode? b, JsonNode? a, string path, string? code, List<ProductDiffEntry> entries)
    {
        if (JsonNode.DeepEquals(b, a))
        {
            return;
        }

        if (b is null || a is null)
        {
            entries.Add(Entry(b is null ? ProductDiffEntry.ClassValue.Added : ProductDiffEntry.ClassValue.Removed, path, code, b, a));
            return;
        }

        if (b is JsonObject bo && a is JsonObject ao)
        {
            foreach (var key in bo.Select(p => p.Key).Union(ao.Select(p => p.Key)))
            {
                bo.TryGetPropertyValue(key, out var bc);
                ao.TryGetPropertyValue(key, out var ac);
                Walk(bc, ac, path + "/" + ThreeWayMerge.Escape(key), code, entries);
            }

            return;
        }

        if (b is JsonArray ba && a is JsonArray aa && ThreeWayMerge.Coded(ba) && ThreeWayMerge.Coded(aa))
        {
            var bm = ThreeWayMerge.Index(ba);
            var am = ThreeWayMerge.Index(aa);
            foreach (var item in ThreeWayMerge.Codes(ba).Union(ThreeWayMerge.Codes(aa)))
            {
                bm.TryGetValue(item, out var bi);
                am.TryGetValue(item, out var ai);
                Walk(bi, ai, $"{path}[{item}]", item, entries);
            }

            return;
        }

        entries.Add(Entry(ProductDiffEntry.ClassValue.Changed, path, code, b, a));
    }

    private static ProductDiffEntry Entry(ProductDiffEntry.ClassValue kind, string path, string? code, JsonNode? before, JsonNode? after) => new()
    {
        Class = kind,
        Path = path.Length == 0 ? "/" : path,
        Code = code,
        Before = before?.ToJsonString(),
        After = after?.ToJsonString(),
    };
}
