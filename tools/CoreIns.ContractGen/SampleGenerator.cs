using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CoreIns.ContractGen;

/// <summary>
/// Builds deterministic, schema-valid sample instances. <c>maximal</c> fills every optional member; minimal sets
/// optional nullable members to null and leaves other optional members out. Values also satisfy the SharedKernel
/// types the members map to (periods end after they start, ids are non-empty UUIDv7, currencies are known).
/// </summary>
internal sealed class SampleGenerator(Documents docs)
{
    private static readonly Dictionary<string, string> Patterns = new(StringComparer.Ordinal)
    {
        ["^[A-Z]{3}$"] = "EUR",
        ["^[A-Z]{2}$"] = "GR",
        ["^[a-z]{2,3}(-[A-Z]{2})?$"] = "el",
        ["^\\d+\\.\\d+$"] = "1.0",
        ["^1\\.\\d+$"] = "1.0",
        ["^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$"] = "1.0",
        ["^[A-Z][A-Za-z0-9]+$"] = "PolicyBound",
        ["^[A-Z]{2,3}_[A-Z0-9_]+$"] = "POL_REFUND_DUE",
        ["^AI-[A-Z]{2,3}-\\d{2,}$"] = "AI-CLM-01",
        ["^RC-[A-Z0-9-]+$"] = "RC-POL-QUOTE",
        ["^OBL-[A-Z0-9-]+$"] = "OBL-GDPR",
        ["^dc\\.[a-z]+\\.[a-z0-9_.-]+\\.v\\d+$"] = "dc.pol.policy.v1",
        ["^[a-z][A-Za-z0-9]*$"] = "policyId",
        ["^[0-9a-f]{64}$"] = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
        ["^[0-9a-f]{32}$"] = "4bf92f3577b34da6a3ce929d0e0e4736",
        ["^(?!0{32})[0-9a-f]{32}$"] = "4bf92f3577b34da6a3ce929d0e0e4736",
        ["^-?(0|[1-9]\\d*)(\\.\\d+)?$"] = "12.50",
        ["^(PTY|PFC|RAT|UW|POL|BIL|CLM|RI|FIN|DOC|CMP|CHN|WRK|PLT|DAT|MIG|MKT)-ERR-[A-Z0-9]+(-[A-Z0-9]+)*$"] = "PLT-ERR-VALIDATION",
        ["^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    };

    public JsonNode? Sample(Located schema, string path, bool maximal, string? prop = null, int depth = 0)
    {
        if (depth > 40)
        {
            throw new InvalidDataException($"{path}: sample recursion too deep.");
        }

        var node = schema.Node;
        if (node["$ref"] is JsonValue r)
        {
            var reference = r.GetValue<string>();
            var target = docs.Resolve(schema.File, reference);
            if (string.Equals(Path.GetFullPath(target.File), Path.GetFullPath(docs.EventsCommon), StringComparison.OrdinalIgnoreCase)
                && Special(reference.Split('/')[^1], path) is { } special)
            {
                return special;
            }

            if (reference.EndsWith("/Unspecified", StringComparison.Ordinal))
            {
                return JsonValue.Create("unspecified");
            }

            return Sample(target, path, maximal, prop, depth + 1);
        }

        if (node["const"] is { } constant)
        {
            return constant.DeepClone();
        }

        if (node["enum"] is JsonArray values)
        {
            return values.First(v => v is not null)!.DeepClone();
        }

        if (node["oneOf"] is JsonArray || node["anyOf"] is JsonArray)
        {
            var alternatives = (node["oneOf"] as JsonArray ?? (JsonArray)node["anyOf"]!).OfType<JsonObject>().ToList();
            if (!(alternatives.All(a => a["required"] is not null && a["type"] is null) && node["properties"] is not null))
            {
                var first = alternatives.FirstOrDefault(a => !(a["type"] is JsonValue t && t.GetValue<string>() == "null")) ?? alternatives[0];
                return Sample(new Located(first, schema.File), path, maximal, prop, depth + 1);
            }
        }

        if (node["allOf"] is JsonArray allOf)
        {
            if (allOf.Count == 1 && node["properties"] is null)
            {
                return Sample(new Located((JsonObject)allOf[0]!, schema.File), path, maximal, prop, depth + 1);
            }

            var mapper = new TypeMapper(docs);
            return Sample(mapper.Merge(schema), path, maximal, prop, depth + 1);
        }

        var type = node["type"];
        var kind = type is JsonArray list ? list.Select(t => t!.GetValue<string>()).First(t => t != "null") : type?.GetValue<string>();
        switch (kind)
        {
            case "string":
                return JsonValue.Create(String(node, path));
            case "boolean":
                return JsonValue.Create(true);
            case "integer":
                return JsonValue.Create(node["minimum"] is JsonValue min ? Math.Max(1, min.GetValue<long>()) : 1);
            case "null":
                return null;
            case "array":
                if (node["items"] is not JsonObject items || items.Count == 0)
                {
                    return new JsonArray(JsonValue.Create("sample"));
                }

                return new JsonArray(Sample(new Located(items, schema.File), path + "[0]", maximal, prop is null ? null : Names.Singular(prop), depth + 1));
            case "object":
            case null when node["properties"] is JsonObject:
                return Object(schema, path, maximal, depth);
            default:
                return JsonValue.Create("unspecified");
        }
    }

    private JsonObject Object(Located schema, string path, bool maximal, int depth)
    {
        var node = schema.Node;
        var props = node["properties"] as JsonObject ?? [];
        var required = (node["required"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        if (node["oneOf"] is JsonArray groups && groups.All(g => g?["required"] is JsonArray && g["type"] is null))
        {
            // Exactly one group: keep the first group's members, leave out members only the other groups name.
            var keep = ((JsonArray)groups[0]!["required"]!).Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var g in groups.Skip(1))
            {
                foreach (var name in ((JsonArray)g!["required"]!).Select(x => x!.GetValue<string>()).Where(n => !keep.Contains(n)))
                {
                    excluded.Add(name);
                }
            }

            required.UnionWith(keep);
        }

        var result = new JsonObject();
        if (props.Count == 0)
        {
            if (node["additionalProperties"] is JsonObject ap && ap.Count > 0)
            {
                result["sample"] = Sample(new Located(ap, schema.File), path + ".sample", maximal, null, depth + 1);
            }
            else
            {
                result["key"] = "value";
            }

            return result;
        }

        foreach (var (name, value) in props)
        {
            if (excluded.Contains(name))
            {
                continue;
            }

            var sub = new Located(value as JsonObject ?? [], schema.File);
            if (required.Contains(name) || maximal)
            {
                // Only the root object varies optional members; nested values are always complete (conditional rules such as
                // RatingSlotDeclaration PINNED -> pinnedArtefactHash, and minProperties, stay satisfied).
                result[name] = Sample(sub, path + "." + name, maximal: true, name, depth + 1);
            }
            else if (AllowsNull(sub))
            {
                result[name] = null;
            }
        }

        return result;
    }

    private static bool AllowsNull(Located schema)
    {
        var n = schema.Node;
        if (n["oneOf"] is JsonArray alts && alts.Any(a => a?["type"] is JsonValue t && t.GetValue<string>() == "null"))
        {
            return true;
        }

        return n["type"] is JsonArray list && list.Any(t => t!.GetValue<string>() == "null");
    }

    private static JsonNode? Special(string def, string path) => def switch
    {
        "Uuid" => JsonValue.Create(Uuid(path)),
        "LocalDate" => JsonValue.Create("2026-10-07"),
        "Instant" => JsonValue.Create("2026-10-07T09:00:00Z"),
        "DatePeriod" => new JsonObject { ["from"] = "2026-01-01", ["to"] = "2027-01-01" },
        "TimeWindow" => new JsonObject { ["from"] = "2026-01-01T00:00:00Z", ["to"] = "2027-01-01T00:00:00Z" },
        "Decimal" => JsonValue.Create("12.50"),
        "Money" => new JsonObject { ["amount"] = "120.50", ["currency"] = "EUR" },
        "BusinessNumber" => JsonValue.Create("SAMPLE-000001"),
        "Code" => JsonValue.Create("SAMPLE_CODE"),
        "Text" => JsonValue.Create("Sample text"),
        "VersionLabel" => JsonValue.Create("1"),
        "ObjectRef" => new JsonObject { ["module"] = "POL", ["type"] = "Policy", ["id"] = Uuid(path + ".id") },
        "OpenObject" => new JsonObject { ["note"] = "sample" },
        "OpenValue" => JsonValue.Create("sample"),
        "MoneyTotals" => new JsonObject { ["total"] = new JsonObject { ["amount"] = "120.50", ["currency"] = "EUR" } },
        "NamedAmounts" => new JsonObject { ["premium"] = new JsonObject { ["amount"] = "120.50", ["currency"] = "EUR" }, ["share"] = "0.25" },
        "Counts" => new JsonObject { ["total"] = 1 },
        "Actor" => new JsonObject { ["kind"] = "USER", ["id"] = "user-sample" },
        _ => null,
    };

    /// <summary>A deterministic UUIDv7-shaped id derived from the sample path.</summary>
    public static string Uuid(string seed)
    {
        var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        return $"0192{hex[..4]}-{hex[4..8]}-7{hex[8..11]}-8{hex[11..14]}-{hex[14..26]}";
    }

    private static string String(JsonObject node, string path)
    {
        if (node["pattern"] is JsonValue p)
        {
            var pattern = p.GetValue<string>();
            if (pattern.StartsWith("^P(?!$)", StringComparison.Ordinal))
            {
                return "P15D";
            }

            if (pattern.Contains("[0-9a-f]{8}-", StringComparison.Ordinal))
            {
                return Uuid(path);
            }

            return Patterns.TryGetValue(pattern, out var value)
                ? value
                : throw new InvalidDataException($"{path}: no sample for string pattern {pattern}; add one to SampleGenerator.Patterns.");
        }

        return node["format"]?.GetValue<string>() switch
        {
            "date" => "2026-10-07",
            "date-time" => "2026-10-07T09:00:00Z",
            "uri" => "https://contracts.invalid/sample",
            "uri-reference" => "/problems/PLT-ERR-VALIDATION",
            _ => "sample",
        };
    }
}
