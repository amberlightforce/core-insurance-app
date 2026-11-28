using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CoreIns.Modules.Product.Domain;

/// <summary>
/// YAML reader and canonical writer for product definitions (BO-PFC-03). The definition's data model is JSON, so YAML is only another notation:
/// the reader maps a YAML document to a JSON tree (plain scalars typed as null, bool or number; quoted scalars are strings), the writer quotes every
/// string that YAML would read as something else, so <c>read(write(json))</c> equals <c>json</c> and the artefact hash survives the round trip.
/// </summary>
internal static partial class ProductYaml
{
    private const int MaxDepth = 64;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "true", "false", "null", "yes", "no", "on", "off", "y", "n", "~" };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_./-]*( [A-Za-z0-9_./-]+)*$")]
    private static partial Regex PlainText();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex Number();

    /// <summary>Reads one YAML document; anchors and aliases are refused (they allow exponential expansion), as are multiple documents.</summary>
    public static JsonNode? Read(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new FormatException("The source is not valid YAML: " + ex.Message, ex);
        }

        if (stream.Documents.Count != 1)
        {
            throw new FormatException("The source must hold exactly one YAML document.");
        }

        return Convert(stream.Documents[0].RootNode, 0);
    }

    public static string Write(JsonNode node)
    {
        var stream = new YamlStream(new YamlDocument(Build(node)));
        using var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        stream.Save(writer, assignAnchors: false);
        return writer.ToString().TrimEnd();
    }

    private static JsonNode? Convert(YamlNode node, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("The YAML document is nested too deeply.");
        }

        if (!node.Anchor.IsEmpty)
        {
            throw new FormatException("YAML anchors and aliases are not supported.");
        }

        switch (node)
        {
            case YamlMappingNode mapping:
            {
                var result = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode { Value: { } name })
                    {
                        throw new FormatException("Mapping keys must be plain text.");
                    }

                    if (result.ContainsKey(name))
                    {
                        throw new FormatException($"Duplicate key '{name}'.");
                    }

                    result[name] = Convert(value, depth + 1);
                }

                return result;
            }

            case YamlSequenceNode sequence:
            {
                var result = new JsonArray();
                foreach (var item in sequence.Children)
                {
                    result.Add(Convert(item, depth + 1));
                }

                return result;
            }

            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                throw new FormatException("Unsupported YAML node.");
        }
    }

    private static JsonValue? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;
        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(text);
        }

        if (text.Length == 0 || text is "null" or "~" or "Null" or "NULL")
        {
            return null;
        }

        if (text is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (text is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        if (Number().IsMatch(text))
        {
            return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                ? JsonValue.Create(whole)
                : JsonValue.Create(decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        return JsonValue.Create(text);
    }

    private static YamlNode Build(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return new YamlScalarNode("null") { Style = ScalarStyle.Plain };
            case JsonObject obj:
            {
                var mapping = new YamlMappingNode();
                foreach (var (key, value) in obj)
                {
                    mapping.Add(Text(key), Build(value));
                }

                return mapping;
            }

            case JsonArray array:
            {
                var sequence = new YamlSequenceNode();
                foreach (var item in array)
                {
                    sequence.Add(Build(item));
                }

                return sequence;
            }

            case JsonValue value when value.TryGetValue<string>(out var s):
                return Text(s);
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                return new YamlScalarNode(flag ? "true" : "false") { Style = ScalarStyle.Plain };
            default:
                return new YamlScalarNode(node.ToJsonString()) { Style = ScalarStyle.Plain };
        }
    }

    private static YamlScalarNode Text(string value) => new(value)
    {
        Style = PlainText().IsMatch(value) && !Reserved.Contains(value) ? ScalarStyle.Plain : ScalarStyle.DoubleQuoted,
    };
}
