using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CoreIns.ContractGen;

/// <summary>A schema node together with the file it lives in (the base for its relative <c>$ref</c>s).</summary>
internal sealed record Located(JsonObject Node, string File);

/// <summary>Loads the contract documents (JSON and YAML, as <see cref="JsonNode"/> trees) and resolves <c>$ref</c>s.</summary>
internal sealed partial class Documents(string contractsRoot)
{
    private readonly Dictionary<string, JsonNode> _cache = new(StringComparer.Ordinal);

    public string ContractsRoot { get; } = Path.GetFullPath(contractsRoot);

    public string EventsRoot => Path.Combine(ContractsRoot, "events");

    public string OpenApiRoot => Path.Combine(ContractsRoot, "openapi");

    public string EventsCommon => Path.Combine(EventsRoot, "common.schema.json");

    public string OpenApiCommon => Path.Combine(OpenApiRoot, "common.yaml");

    public JsonNode Load(string file)
    {
        var full = Path.GetFullPath(file);
        if (!_cache.TryGetValue(full, out var node))
        {
            var text = File.ReadAllText(full);
            node = full.EndsWith(".yaml", StringComparison.Ordinal) || full.EndsWith(".yml", StringComparison.Ordinal)
                ? YamlToJson(text, full)
                : JsonNode.Parse(text) ?? throw new InvalidDataException($"{full} is empty.");
            _cache[full] = node;
        }

        return node;
    }

    public JsonObject LoadObject(string file) => Load(file) as JsonObject ?? throw new InvalidDataException($"{file} is not an object.");

    /// <summary>Resolves <paramref name="reference"/> (relative file + JSON pointer fragment) against <paramref name="baseFile"/>.</summary>
    public Located Resolve(string baseFile, string reference)
    {
        var hash = reference.IndexOf('#', StringComparison.Ordinal);
        var filePart = hash < 0 ? reference : reference[..hash];
        var pointer = hash < 0 ? string.Empty : reference[(hash + 1)..];
        var file = filePart.Length == 0
            ? baseFile
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(baseFile)!, filePart.Replace('/', Path.DirectorySeparatorChar)));
        var node = Load(file);
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject obj when obj[token] is { } child => child,
                JsonArray arr when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < arr.Count && arr[i] is { } item => item,
                _ => throw new InvalidDataException($"$ref '{reference}' from {baseFile} does not resolve (at '{token}')."),
            };
        }

        return new Located(node as JsonObject ?? throw new InvalidDataException($"$ref '{reference}' is not a schema object."), file);
    }

    /// <summary>Follows <c>$ref</c> chains until a node without <c>$ref</c>.</summary>
    public Located Deref(Located schema)
    {
        var current = schema;
        for (var guard = 0; current.Node["$ref"] is JsonValue r; guard++)
        {
            if (guard > 20)
            {
                throw new InvalidDataException("$ref chain too long.");
            }

            current = Resolve(current.File, r.GetValue<string>());
        }

        return current;
    }

    private static JsonNode YamlToJson(string text, string file)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"{file}: {ex.Message}", ex);
        }

        return Convert(stream.Documents[0].RootNode) ?? throw new InvalidDataException($"{file} is empty.");
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => ConvertMap(map),
        YamlSequenceNode seq => new JsonArray([.. seq.Children.Select(Convert)]),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => throw new InvalidDataException($"Unsupported YAML node {node.NodeType} at {node.Start}."),
    };

    private static JsonObject ConvertMap(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map.Children)
        {
            var name = ((YamlScalarNode)key).Value ?? string.Empty;
            obj[name] = Convert(value);
        }

        return obj;
    }

    private static JsonValue? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(value);
        }

        return value switch
        {
            "" or "~" or "null" or "Null" or "NULL" => null,
            "true" or "True" or "TRUE" => JsonValue.Create(true),
            "false" or "False" or "FALSE" => JsonValue.Create(false),
            _ when IntPattern().IsMatch(value) => JsonValue.Create(long.Parse(value, CultureInfo.InvariantCulture)),
            // Other plain scalars (including YAML floats such as 1.0) stay text: no schema keyword the generator reads is a
            // floating-point number, and contracts carry no binary floating point.
            _ => JsonValue.Create(value),
        };
    }

    [GeneratedRegex("^[-+]?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntPattern();
}
