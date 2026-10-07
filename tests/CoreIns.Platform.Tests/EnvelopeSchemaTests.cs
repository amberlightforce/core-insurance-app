using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoreIns.Platform.Events;

namespace CoreIns.Platform.Tests;

/// <summary>
/// The C# envelope serialises to JSON that validates against contracts/events/envelope.schema.json (review m8).
/// A small validator for exactly the keywords the envelope schema uses keeps this a plain unit test with no new
/// package (ADR §2 rule 11); it fails if the schema starts using a keyword it does not implement, so it can never pass
/// silently. Full event schemas (payloads) stay with contracts/events/validate.py in CI.
/// </summary>
public sealed class EnvelopeSchemaTests
{
    private static readonly HashSet<string> Annotations = ["$schema", "$id", "title", "description", "format", "examples", "$defs"];

    private static readonly HashSet<string> Supported =
    [
        "type", "required", "properties", "pattern", "enum", "minimum", "minLength", "maxLength", "oneOf", "$ref",
        "additionalProperties", "propertyNames", "minProperties", "dependentRequired",
    ];

    private static readonly JsonObject Schema = LoadSchema();

    [Fact]
    public void The_validator_understands_every_keyword_of_the_envelope_schema() =>
        Keywords(Schema).Where(k => !Supported.Contains(k) && !Annotations.Contains(k) && !k.StartsWith("x-", StringComparison.Ordinal))
            .ShouldBeEmpty();

    [Fact]
    public void A_set_event_envelope_validates()
    {
        var wire = EnvelopeTests.Valid(EnvelopeTests.ChargeDeltaEmitted).ToWireJson();
        Validate(Wire(wire), Schema, "$").ShouldBeEmpty();
        wire["index"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void A_plain_event_envelope_with_null_causation_and_ai_validates()
    {
        var wire = EnvelopeTests.Valid(EnvelopeTests.PartyEvent).ToWireJson();
        wire.ContainsKey("aiInteractionId").ShouldBeTrue();
        wire.ContainsKey("set_id").ShouldBeFalse();
        Validate(Wire(wire), Schema, "$").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("jurisdiction")]
    [InlineData("aiInteractionId")]
    [InlineData("businessKeys")]
    public void Missing_required_fields_are_caught_by_the_schema(string field)
    {
        var wire = EnvelopeTests.Valid(EnvelopeTests.PartyEvent).ToWireJson();
        wire.Remove(field);
        Validate(Wire(wire), Schema, "$").ShouldNotBeEmpty();
    }

    [Fact]
    public void The_validator_rejects_bad_values()
    {
        var wire = EnvelopeTests.Valid(EnvelopeTests.PartyEvent).ToWireJson();
        wire["correlationId"] = new string('0', 32);
        wire["origin"] = "BATCH";
        wire["set_id"] = Guid.NewGuid().ToString();
        var problems = Validate(Wire(wire), Schema, "$");
        problems.ShouldContain(p => p.Contains("correlationId"));
        problems.ShouldContain(p => p.Contains("origin"));
        problems.ShouldContain(p => p.Contains("set_size"));
    }

    private static JsonNode? Wire(JsonObject envelope) => JsonNode.Parse(envelope.ToJsonString());

    private static List<string> Validate(JsonNode? value, JsonObject schema, string path)
    {
        var problems = new List<string>();
        if (schema["$ref"] is JsonValue reference)
        {
            var name = reference.GetValue<string>()["#/$defs/".Length..];
            problems.AddRange(Validate(value, Schema["$defs"]![name]!.AsObject(), path));
        }

        if (schema["oneOf"] is JsonArray oneOf && oneOf.Count(option => Validate(value, option!.AsObject(), path).Count == 0) != 1)
        {
            problems.Add($"{path}: must match exactly one oneOf option");
        }

        if (schema["type"] is JsonValue type && !HasType(value, type.GetValue<string>()))
        {
            problems.Add($"{path}: must be {type}");
            return problems;
        }

        if (schema["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, value)))
        {
            problems.Add($"{path}: not one of the allowed values");
        }

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            if (schema["pattern"] is JsonValue pattern && !Regex.IsMatch(text, pattern.GetValue<string>()))
            {
                problems.Add($"{path}: does not match {pattern}");
            }

            if (schema["minLength"] is JsonValue min && text.Length < min.GetValue<int>())
            {
                problems.Add($"{path}: too short");
            }

            if (schema["maxLength"] is JsonValue max && text.Length > max.GetValue<int>())
            {
                problems.Add($"{path}: too long");
            }
        }

        if (value is JsonValue number && schema["minimum"] is JsonValue minimum && number.TryGetValue<long>(out var n) && n < minimum.GetValue<long>())
        {
            problems.Add($"{path}: below {minimum}");
        }

        if (value is JsonObject obj)
        {
            foreach (var required in schema["required"]?.AsArray() ?? [])
            {
                if (!obj.ContainsKey(required!.GetValue<string>()))
                {
                    problems.Add($"{path}.{required}: required");
                }
            }

            if (schema["minProperties"] is JsonValue minProperties && obj.Count < minProperties.GetValue<int>())
            {
                problems.Add($"{path}: too few properties");
            }

            if (schema["dependentRequired"] is JsonObject dependent)
            {
                foreach (var (name, needs) in dependent)
                {
                    if (obj.ContainsKey(name))
                    {
                        problems.AddRange(needs!.AsArray().Where(need => !obj.ContainsKey(need!.GetValue<string>()))
                            .Select(need => $"{path}.{need}: required with {name}"));
                    }
                }
            }

            var properties = schema["properties"] as JsonObject;
            foreach (var (name, member) in obj)
            {
                if (schema["propertyNames"]?["pattern"] is JsonValue namePattern && !Regex.IsMatch(name, namePattern.GetValue<string>()))
                {
                    problems.Add($"{path}.{name}: property name does not match {namePattern}");
                }

                if (properties?[name] is JsonObject propertySchema)
                {
                    problems.AddRange(Validate(member, propertySchema, $"{path}.{name}"));
                }
                else if (schema["additionalProperties"] is JsonObject additional)
                {
                    problems.AddRange(Validate(member, additional, $"{path}.{name}"));
                }
                else if (schema["additionalProperties"] is JsonValue allowed && !allowed.GetValue<bool>())
                {
                    problems.Add($"{path}.{name}: not allowed");
                }
            }
        }

        return problems;
    }

    private static bool HasType(JsonNode? value, string type) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "null" => value is null,
        "string" => value is JsonValue v && v.TryGetValue<string>(out _),
        "integer" => value is JsonValue v && v.TryGetValue<long>(out _),
        "boolean" => value is JsonValue v && v.TryGetValue<bool>(out _),
        _ => throw new InvalidOperationException($"type {type} is not supported"),
    };

    private static IEnumerable<string> Keywords(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(pair =>
            pair.Key is "properties" or "$defs" or "dependentRequired"
                ? pair.Value!.AsObject().SelectMany(inner => Keywords(inner.Value)).Prepend(pair.Key)
                : Keywords(pair.Value).Prepend(pair.Key)),
        JsonArray array => array.SelectMany(Keywords),
        _ => [],
    };

    private static JsonObject LoadSchema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreIns.sln")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory!.FullName, "contracts", "events", "envelope.schema.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }
}
