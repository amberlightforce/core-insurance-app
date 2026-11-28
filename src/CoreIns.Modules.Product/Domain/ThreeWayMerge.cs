using System.Text.Json.Nodes;

namespace CoreIns.Modules.Product.Domain;

/// <summary>One path both sides changed differently. Paths use JSON pointers; an item of a coded list is written <c>/elements[code]</c>.</summary>
internal sealed record MergeConflict(string Path, string? Base, string? Ours, string? Theirs);

internal sealed record MergeResult(JsonNode Merged, IReadOnlyList<MergeConflict> Conflicts)
{
    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>
/// Pure three-way merge of product definitions as JSON (BO-PFC-02, REQ-PFC-182..188). <c>base</c> is the common ancestor (the change set's
/// base version), <c>ours</c> the draft, <c>theirs</c> the newer version. Objects merge per property; arrays whose items are all objects with a
/// string <c>code</c> (elements, coverages, chargeTypes, ...) merge per code; every other array or scalar is atomic. A path changed on only one
/// side, or changed identically on both, merges silently; changed differently on both (including change versus delete) is a conflict.
/// </summary>
internal static class ThreeWayMerge
{
    public static MergeResult Merge(JsonNode @base, JsonNode ours, JsonNode theirs, IReadOnlyCollection<string>? oursWinsPaths = null)
    {
        var conflicts = new List<MergeConflict>();
        var merged = MergeNode(@base, ours, theirs, string.Empty, oursWinsPaths ?? [], conflicts);
        return new MergeResult(merged ?? ours.DeepClone(), conflicts);
    }

    private static JsonNode? MergeNode(JsonNode? b, JsonNode? o, JsonNode? t, string path, IReadOnlyCollection<string> oursWins, List<MergeConflict> conflicts)
    {
        if (Same(o, t))
        {
            return Clone(o);
        }

        if (Same(b, o))
        {
            return Clone(t);
        }

        if (Same(b, t) || oursWins.Contains(path))
        {
            return Clone(o);
        }

        // Both sides changed it differently: recurse where all three are structured the same way, else it is a conflict.
        if (o is JsonObject oo && t is JsonObject to && (b is null || b is JsonObject))
        {
            var bo = b as JsonObject;
            var result = new JsonObject();
            foreach (var key in oo.Select(p => p.Key).Union(to.Select(p => p.Key)).Union(bo?.Select(p => p.Key) ?? []))
            {
                var child = MergeNode(Get(bo, key), Get(oo, key), Get(to, key), path + "/" + Escape(key), oursWins, conflicts);
                if (child is not null || (oo.ContainsKey(key) && to.ContainsKey(key)))
                {
                    result[key] = child;
                }
            }

            return result;
        }

        if (o is JsonArray oa && t is JsonArray ta && (b is null || b is JsonArray) && Coded(oa) && Coded(ta) && (b is null || Coded((JsonArray)b)))
        {
            return MergeCoded(b as JsonArray ?? [], oa, ta, path, oursWins, conflicts);
        }

        conflicts.Add(new MergeConflict(path.Length == 0 ? "/" : path, b?.ToJsonString(), o?.ToJsonString(), t?.ToJsonString()));
        return Clone(o);
    }

    private static JsonArray MergeCoded(JsonArray b, JsonArray o, JsonArray t, string path, IReadOnlyCollection<string> oursWins, List<MergeConflict> conflicts)
    {
        var bm = Index(b);
        var om = Index(o);
        var tm = Index(t);
        var codes = Codes(b).SequenceEqual(Codes(t)) ? Codes(o).Union(Codes(t)) : Codes(t).Union(Codes(o));
        var result = new JsonArray();
        foreach (var code in codes.Union(Codes(b)))
        {
            var merged = MergeNode(bm.GetValueOrDefault(code), om.GetValueOrDefault(code), tm.GetValueOrDefault(code), $"{path}[{code}]", oursWins, conflicts);
            if (merged is not null)
            {
                result.Add(merged);
            }
        }

        return result;
    }

    private static bool Coded(JsonArray array) =>
        array.All(i => i is JsonObject obj && obj["code"] is JsonValue v && v.TryGetValue<string>(out _))
        && Codes(array).Count() == array.Count;

    private static IEnumerable<string> Codes(JsonArray array) => array.Select(i => (string)i!["code"]!);

    private static Dictionary<string, JsonNode> Index(JsonArray array) => array.ToDictionary(i => (string)i!["code"]!, i => i!);

    private static JsonNode? Get(JsonObject? obj, string key) => obj is not null && obj.TryGetPropertyValue(key, out var v) ? v : null;

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    private static string Escape(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
