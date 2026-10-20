using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoreIns.Platform.Events;

namespace CoreIns.Testing.Contracts;

/// <summary>One schema violation: <see cref="Key"/> is stable (path with array indexes removed + keyword), <see cref="Detail"/> is for humans.</summary>
public sealed record SchemaViolation(string Key, string Detail);

/// <summary>
/// Validates an event as it goes on the wire (<see cref="EventEnvelope.ToWireJson"/>) against its JSON schema in
/// <c>contracts/events/&lt;module&gt;/&lt;EventType&gt;.v&lt;major&gt;.schema.json</c> (NX-TST-05). A small validator for exactly the
/// keywords the event schemas use (no new package, ADR §2 rule 11): a schema using any other keyword makes the validator
/// throw, so it can never pass silently. <c>unevaluatedProperties</c> is honoured across <c>allOf</c> and <c>$ref</c>.
/// </summary>
public sealed class EventSchemaValidator
{
    private static readonly HashSet<string> Annotations = ["$schema", "$id", "title", "description", "format", "examples", "$defs", "$comment"];

    private static readonly Regex VersionMajor = new(@"^(\d+)\.", RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<string, JsonNode> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Regex> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _schemaFiles = new(StringComparer.Ordinal);

    /// <summary>Loads the schema index from a <c>contracts/events</c> directory.</summary>
    public EventSchemaValidator(string eventsDir)
    {
        var root = Path.GetFullPath(eventsDir);
        foreach (var file in Directory.EnumerateFiles(root, "*.v*.schema.json", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file); // <EventType>.v<major>.schema.json
            _schemaFiles[name[..^".schema.json".Length]] = file;
        }
    }

    /// <summary>The validator over the repository's contracts/events (found above the test assembly).</summary>
    public static EventSchemaValidator ForRepository { get; } = new(FindEventsDirectory());

    /// <summary>Number of event schemas found.</summary>
    public int SchemaCount => _schemaFiles.Count;

    /// <summary>Validates one wire-format event; empty when valid.</summary>
    public IReadOnlyList<SchemaViolation> Validate(JsonObject wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var type = wire["eventType"]?.GetValue<string>() ?? "?";
        var version = wire["schemaVersion"]?.GetValue<string>() ?? "?";
        var major = VersionMajor.Match(version) is { Success: true } m ? m.Groups[1].Value : version;
        if (!_schemaFiles.TryGetValue($"{type}.v{major}", out var file))
        {
            return [new SchemaViolation("$: no event schema", $"no schema {type}.v{major}.schema.json under contracts/events")];
        }

        var errors = new List<SchemaViolation>();
        Eval(wire, Load(file), file, "$", errors, []);
        return errors;
    }

    /// <summary>Validates an envelope (as written to the outbox).</summary>
    public IReadOnlyList<SchemaViolation> Validate(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Validate(envelope.ToWireJson());
    }

    private JsonNode Load(string file) => _documents.GetOrAdd(file, f => JsonNode.Parse(File.ReadAllText(f))!);

    private static string FindEventsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "contracts", "events")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("contracts/events not found above " + AppContext.BaseDirectory)
            : Path.Combine(directory.FullName, "contracts", "events");
    }

    private (JsonNode Schema, string File) Resolve(string reference, string file)
    {
        var hash = reference.IndexOf('#', StringComparison.Ordinal);
        var filePart = hash < 0 ? reference : reference[..hash];
        var pointer = hash < 0 ? string.Empty : reference[(hash + 1)..];
        var target = filePart.Length == 0 ? file : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, filePart));
        var node = Load(target);
        foreach (var part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node[part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)]
                ?? throw new InvalidOperationException($"Unresolvable $ref '{reference}' in {file}");
        }

        return (node, target);
    }

    /// <summary>Evaluates <paramref name="instance"/> against a schema; adds failures to <paramref name="errors"/> and the object members the schema evaluated to <paramref name="evaluated"/>.</summary>
    private bool Eval(JsonNode? instance, JsonNode schemaNode, string file, string path, List<SchemaViolation> errors, HashSet<string> evaluated)
    {
        if (schemaNode is JsonValue boolean)
        {
            if (boolean.GetValue<bool>())
            {
                return true;
            }

            errors.Add(new SchemaViolation($"{Key(path)}: false schema", $"{path}: no value is allowed"));
            return false;
        }

        var schema = schemaNode.AsObject();
        var before = errors.Count;
        var kind = instance?.GetValueKind() ?? JsonValueKind.Null;
        JsonNode? unevaluated = null;

        foreach (var (keyword, value) in schema)
        {
            if (value is null)
            {
                continue;
            }

            switch (keyword)
            {
                case "$ref":
                    var (target, targetFile) = Resolve(value.GetValue<string>(), file);
                    Eval(instance, target, targetFile, path, errors, evaluated);
                    break;
                case "allOf":
                    foreach (var branch in value.AsArray())
                    {
                        Eval(instance, branch!, file, path, errors, evaluated);
                    }

                    break;
                case "anyOf":
                case "oneOf":
                    Alternatives(keyword, value.AsArray(), instance, file, path, errors, evaluated);
                    break;
                case "if":
                    var condition = Eval(instance, value, file, path, [], []);
                    if (condition && schema["then"] is { } then)
                    {
                        Eval(instance, then, file, path, errors, evaluated);
                    }
                    else if (!condition && schema["else"] is { } otherwise)
                    {
                        Eval(instance, otherwise, file, path, errors, evaluated);
                    }

                    break;
                case "then":
                case "else":
                    break; // handled by "if"
                case "type":
                    TypeCheck(value, kind, instance, path, errors);
                    break;
                case "const":
                    if (!JsonNode.DeepEquals(instance, value))
                    {
                        errors.Add(new SchemaViolation($"{Key(path)}: const", $"{path}: expected {value.ToJsonString()}, got {Describe(instance)}"));
                    }

                    break;
                case "enum":
                    if (!value.AsArray().Any(v => JsonNode.DeepEquals(instance, v)))
                    {
                        errors.Add(new SchemaViolation($"{Key(path)}: enum", $"{path}: {Describe(instance)} is not one of {value.ToJsonString()}"));
                    }

                    break;
                case "pattern":
                    if (kind == JsonValueKind.String && !Pattern(value.GetValue<string>()).IsMatch(instance!.GetValue<string>()))
                    {
                        errors.Add(new SchemaViolation($"{Key(path)}: pattern", $"{path}: {Describe(instance)} does not match {value.GetValue<string>()}"));
                    }

                    break;
                case "minLength":
                    if (kind == JsonValueKind.String && instance!.GetValue<string>().Length < value.GetValue<int>())
                    {
                        errors.Add(new SchemaViolation($"{Key(path)}: minLength", $"{path}: shorter than {value}"));
                    }

                    break;
                case "maxLength":
                    if (kind == JsonValueKind.String && instance!.GetValue<string>().Length > value.GetValue<int>())
                    {
                        errors.Add(new SchemaViolation($"{Key(path)}: maxLength", $"{path}: longer than {value}"));
                    }

                    break;
                case "minimum":
                    if (kind == JsonValueKind.Number && Num(instance!) < Num(value))
                    {
                        errors.Add(new SchemaViolation($"{Key(path)}: minimum", $"{path}: below {value}"));
                    }

                    break;
                case "items":
                    if (instance is JsonArray array)
                    {
                        for (var i = 0; i < array.Count; i++)
                        {
                            Eval(array[i], value, file, $"{path}[{i}]", errors, []);
                        }
                    }

                    break;
                case "required":
                    if (instance is JsonObject requiredOwner)
                    {
                        foreach (var name in value.AsArray())
                        {
                            if (!requiredOwner.ContainsKey(name!.GetValue<string>()))
                            {
                                errors.Add(new SchemaViolation($"{Key(path)}: required {name}", $"{path}: missing required member '{name}'"));
                            }
                        }
                    }

                    break;
                case "properties":
                    if (instance is JsonObject owner)
                    {
                        foreach (var (name, sub) in value.AsObject())
                        {
                            if (owner.TryGetPropertyValue(name, out var member))
                            {
                                evaluated.Add(name);
                                Eval(member, sub!, file, $"{path}.{name}", errors, []);
                            }
                        }
                    }

                    break;
                case "additionalProperties":
                    if (instance is JsonObject extra)
                    {
                        var declared = schema["properties"]?.AsObject();
                        foreach (var (name, member) in extra)
                        {
                            if (declared is not null && declared.ContainsKey(name))
                            {
                                continue;
                            }

                            evaluated.Add(name);
                            if (value is JsonValue { } closed && !closed.GetValue<bool>())
                            {
                                errors.Add(new SchemaViolation($"{Key(path)}: additionalProperties {name}", $"{path}: member '{name}' is not allowed by the schema"));
                                continue;
                            }

                            Eval(member, value, file, $"{path}.{name}", errors, []);
                        }
                    }

                    break;
                case "propertyNames":
                    if (instance is JsonObject named)
                    {
                        foreach (var (name, _) in named)
                        {
                            Eval(JsonValue.Create(name), value, file, path + "{name}", errors, []);
                        }
                    }

                    break;
                case "minProperties":
                    if (instance is JsonObject counted && counted.Count < value.GetValue<int>())
                    {
                        errors.Add(new SchemaViolation($"{Key(path)}: minProperties", $"{path}: fewer than {value} members"));
                    }

                    break;
                case "dependentRequired":
                    if (instance is JsonObject dependent)
                    {
                        foreach (var (name, needs) in value.AsObject())
                        {
                            if (dependent.ContainsKey(name) && needs!.AsArray().Any(n => !dependent.ContainsKey(n!.GetValue<string>())))
                            {
                                errors.Add(new SchemaViolation($"{Key(path)}: dependentRequired {name}", $"{path}: '{name}' requires {needs.ToJsonString()}"));
                            }
                        }
                    }

                    break;
                case "unevaluatedProperties":
                    unevaluated = value; // needs everything else evaluated first
                    break;
                default:
                    if (!Annotations.Contains(keyword) && !keyword.StartsWith("x-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"EventSchemaValidator does not implement the JSON Schema keyword '{keyword}' (in {file}); implement it before relying on it.");
                    }

                    break;
            }
        }

        if (unevaluated is not null && instance is JsonObject all)
        {
            foreach (var (name, member) in all)
            {
                if (!evaluated.Add(name))
                {
                    continue;
                }

                if (unevaluated is JsonValue flag)
                {
                    if (!flag.GetValue<bool>())
                    {
                        errors.Add(new SchemaViolation($"{Key(path)}: unevaluatedProperties {name}", $"{path}: member '{name}' is not allowed by the schema"));
                    }
                }
                else
                {
                    Eval(member, unevaluated, file, $"{path}.{name}", errors, []);
                }
            }
        }

        return errors.Count == before;
    }

    private void Alternatives(string keyword, JsonArray branches, JsonNode? instance, string file, string path, List<SchemaViolation> errors, HashSet<string> evaluated)
    {
        var matched = 0;
        var branchErrors = new List<string>();
        foreach (var branch in branches)
        {
            var local = new List<SchemaViolation>();
            var seen = new HashSet<string>();
            if (Eval(instance, branch!, file, path, local, seen))
            {
                matched++;
                evaluated.UnionWith(seen);
            }
            else
            {
                branchErrors.Add(string.Join("; ", local.Select(v => v.Detail).Take(2)));
            }
        }

        if (matched == 0 || (keyword == "oneOf" && matched > 1))
        {
            errors.Add(new SchemaViolation($"{Key(path)}: {keyword}", $"{path}: {keyword} matched {matched} branches ({string.Join(" | ", branchErrors)})"));
        }
    }

    private static void TypeCheck(JsonNode typeNode, JsonValueKind kind, JsonNode? instance, string path, List<SchemaViolation> errors)
    {
        var allowed = typeNode is JsonArray list ? list.Select(t => t!.GetValue<string>()).ToArray() : [typeNode.GetValue<string>()];
        var ok = allowed.Any(t => t switch
        {
            "string" => kind == JsonValueKind.String,
            "number" => kind == JsonValueKind.Number,
            "integer" => kind == JsonValueKind.Number && Num(instance!) == decimal.Truncate(Num(instance!)),
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "null" => kind == JsonValueKind.Null,
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            _ => throw new InvalidOperationException($"Unknown JSON Schema type '{t}'"),
        });
        if (!ok)
        {
            errors.Add(new SchemaViolation($"{Key(path)}: type {string.Join('|', allowed)}", $"{path}: expected {string.Join('|', allowed)}, got {kind}"));
        }
    }

    private static decimal Num(JsonNode node) =>
        decimal.Parse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    private Regex Pattern(string pattern) => _patterns.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant));

    private static string Key(string path) => Regex.Replace(path, @"\[\d+\]", "[]");

    private static string Describe(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length > 60 ? text[..60] + "..." : text;
    }
}
