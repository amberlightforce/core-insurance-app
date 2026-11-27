using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CoreIns.Modules.Data.Domain;

/// <summary>
/// The data-contract document (BO-DAT-01): the payload shape DAT expects from one event type. Deliberately small:
/// <c>{ "name": "pol.PolicyIssued", "major": 1, "requiredFields": ["policyId"], "properties": { "policyId": "string" } }</c>.
/// Property types are JSON kinds: string, number, integer, boolean, object, array.
/// </summary>
internal sealed partial record ContractDocument(string Name, int? Major, IReadOnlyList<string> RequiredFields, IReadOnlyDictionary<string, string> Properties)
{
    private static readonly string[] Kinds = ["string", "number", "integer", "boolean", "object", "array"];

    /// <summary>Parses and validates the document; returns the problems found.</summary>
    public static (ContractDocument? Document, IReadOnlyList<string> Problems) Parse(JsonElement element)
    {
        var problems = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, ["contractDocument must be an object"]);
        }

        var name = element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (name is null || !NamePattern().IsMatch(name))
        {
            problems.Add("name must be <module>.<EventType>, e.g. pol.PolicyIssued");
        }

        int? major = null;
        if (element.TryGetProperty("major", out var m))
        {
            if (m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var parsed) && parsed >= 1)
            {
                major = parsed;
            }
            else
            {
                problems.Add("major must be an integer >= 1");
            }
        }

        var required = new List<string>();
        if (element.TryGetProperty("requiredFields", out var r))
        {
            if (r.ValueKind != JsonValueKind.Array || r.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())))
            {
                problems.Add("requiredFields must be an array of field names");
            }
            else
            {
                required.AddRange(r.EnumerateArray().Select(x => x.GetString()!));
            }
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty("properties", out var p))
        {
            if (p.ValueKind != JsonValueKind.Object)
            {
                problems.Add("properties must be an object of field name to type");
            }
            else
            {
                foreach (var property in p.EnumerateObject())
                {
                    var kind = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    if (kind is null || !Kinds.Contains(kind, StringComparer.Ordinal))
                    {
                        problems.Add($"property '{property.Name}' must have one of the types {string.Join(", ", Kinds)}");
                    }
                    else
                    {
                        properties[property.Name] = kind;
                    }
                }
            }
        }

        foreach (var field in required.Where(f => !properties.ContainsKey(f)))
        {
            properties[field] = "any";
        }

        return problems.Count > 0 ? (null, problems) : (new ContractDocument(name!, major, required, properties), []);
    }

    /// <summary>Checks a payload against the contract; returns the problems found (empty when valid).</summary>
    public IReadOnlyList<string> Validate(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var problems = new List<string>();
        foreach (var field in RequiredFields)
        {
            if (!payload.TryGetPropertyValue(field, out var value) || value is null)
            {
                problems.Add($"required payload field '{field}' is missing");
            }
        }

        foreach (var (field, kind) in Properties.Where(p => p.Value != "any"))
        {
            if (payload.TryGetPropertyValue(field, out var value) && value is not null && !Matches(value, kind))
            {
                problems.Add($"payload field '{field}' is not of type {kind}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Breaking changes of <paramref name="next"/> relative to <paramref name="previous"/>: a removed property, a changed type, or
    /// a newly required field. Relaxing a required field is compatible.
    /// </summary>
    public static IReadOnlyList<string> BreakingChanges(ContractDocument previous, ContractDocument next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        var changes = new List<string>();
        foreach (var (field, kind) in previous.Properties)
        {
            if (!next.Properties.TryGetValue(field, out var nextKind))
            {
                changes.Add($"property '{field}' was removed");
            }
            else if (nextKind != kind && kind != "any")
            {
                changes.Add($"property '{field}' changed type from {kind} to {nextKind}");
            }
        }

        changes.AddRange(next.RequiredFields.Except(previous.RequiredFields, StringComparer.Ordinal).Select(f => $"field '{f}' became required"));
        return changes;
    }

    private static bool Matches(JsonNode value, string kind) => kind switch
    {
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "number" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<decimal>(out var d) && d == decimal.Truncate(d),
        "boolean" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        _ => true,
    };

    [GeneratedRegex(@"^[a-z]{3}\.[A-Z][A-Za-z0-9]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
