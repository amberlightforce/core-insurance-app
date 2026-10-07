using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.SharedKernel.Json;

/// <summary>
/// RFC 8785 JSON Canonicalization Scheme (JCS) and SHA-256 over it (D-ARC-12): the basis of configuration, artefact,
/// approval-content and audit hashes. Object members are sorted by their UTF-16 code units, strings use the minimal
/// ECMAScript escaping, numbers use ECMAScript <c>Number.prototype.toString</c> (see <see cref="Es6Number"/>), and no
/// whitespace is emitted. Input must be I-JSON: duplicate member names, lone surrogates and numbers outside binary64
/// are rejected with <see cref="CanonicalJsonException"/>.
/// <para>Money and other decimals belong in JSON as decimal strings (contracts/events): a JSON number passes through
/// binary64 under RFC 8785 and keeps at most 17 significant digits.</para>
/// </summary>
public static class CanonicalJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 256,
    };

    /// <summary>Canonical UTF-8 bytes of a UTF-8 JSON text.</summary>
    public static byte[] Canonicalize(ReadOnlyMemory<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new CanonicalJsonException("The input is not valid JSON.", ex);
        }

        using (document)
        {
            return Canonicalize(document.RootElement);
        }
    }

    /// <summary>Canonical text of a JSON text.</summary>
    public static string Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Encoding.UTF8.GetString(Canonicalize(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>Canonical UTF-8 bytes of a parsed element.</summary>
    public static byte[] Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(element, builder);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Canonical UTF-8 bytes of a <see cref="JsonNode"/> tree (null = JSON <c>null</c>).</summary>
    public static byte[] Canonicalize(JsonNode? node)
    {
        var json = node is null ? "null" : node.ToJsonString();
        return Canonicalize(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Serialises <paramref name="value"/> with <see cref="SharedKernelJson.Options"/> (or the given options) and canonicalises it.</summary>
    public static byte[] Serialize<T>(T value, JsonSerializerOptions? options = null) =>
        Canonicalize(JsonSerializer.SerializeToUtf8Bytes(value, options ?? SharedKernelJson.Options));

    /// <summary>SHA-256 of the canonical form of a JSON text.</summary>
    public static Sha256Hash Hash(string json) => Sha256Hash.Compute(Encoding.UTF8.GetBytes(Canonicalize(json)));

    /// <summary>SHA-256 of the canonical form of a serialised value.</summary>
    public static Sha256Hash HashOf<T>(T value, JsonSerializerOptions? options = null) => Sha256Hash.Compute(Serialize(value, options));

    /// <summary>SHA-256 of the canonical form of a node tree.</summary>
    public static Sha256Hash Hash(JsonNode? node) => Sha256Hash.Compute(Canonicalize(node));

    private static void Write(JsonElement element, StringBuilder output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, output);
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        output.Append(',');
                    }

                    first = false;
                    Write(item, output);
                }

                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(GetString(element), output);
                break;
            case JsonValueKind.Number:
                output.Append(Es6Number.Canonicalize(element.GetRawText()));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new CanonicalJsonException($"Unexpected JSON value kind {element.ValueKind}.");
        }
    }

    private static void WriteObject(JsonElement element, StringBuilder output)
    {
        var members = new List<(string Name, JsonElement Value)>();
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            EnsureWellFormed(name);
            members.Add((name, property.Value));
        }

        members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 1; i < members.Count; i++)
        {
            if (string.Equals(members[i - 1].Name, members[i].Name, StringComparison.Ordinal))
            {
                throw new CanonicalJsonException($"Duplicate member name '{members[i].Name}' (RFC 8785 requires I-JSON).");
            }
        }

        output.Append('{');
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                output.Append(',');
            }

            WriteString(members[i].Name, output);
            output.Append(':');
            Write(members[i].Value, output);
        }

        output.Append('}');
    }

    private static string GetString(JsonElement element)
    {
        string value;
        try
        {
            value = element.GetString()!;
        }
        catch (InvalidOperationException ex)
        {
            throw new CanonicalJsonException("A string is not well-formed Unicode (lone surrogate).", ex);
        }

        EnsureWellFormed(value);
        return value;
    }

    private static void EnsureWellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                throw new CanonicalJsonException("A string is not well-formed Unicode (lone surrogate).");
            }
        }
    }

    /// <summary>RFC 8785 §3.2.2.2 string serialisation.</summary>
    private static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
    }
}

/// <summary>The input cannot be canonicalised under RFC 8785 (not I-JSON).</summary>
public sealed class CanonicalJsonException : FormatException
{
    /// <summary>Creates the exception.</summary>
    public CanonicalJsonException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public CanonicalJsonException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public CanonicalJsonException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
