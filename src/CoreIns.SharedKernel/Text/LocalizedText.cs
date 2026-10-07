using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel;

/// <summary>The two mandatory languages (contract §3.9.5): Greek (language of record in Greece) and English.</summary>
public enum Language
{
    /// <summary>Greek (<c>el</c>), the default and the language of record for Greek documents.</summary>
    El,

    /// <summary>English (<c>en</c>).</summary>
    En,
}

/// <summary>Helpers for <see cref="Language"/>.</summary>
public static class Languages
{
    /// <summary>The BCP 47 primary tag (<c>el</c>, <c>en</c>).</summary>
    public static string ToTag(this Language language) => language switch
    {
        Language.El => "el",
        Language.En => "en",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>Reads a BCP 47 tag (<c>el</c>, <c>el-GR</c>, <c>en</c>, <c>en-GB</c>, any case); other languages are not supported.</summary>
    public static bool TryParse([NotNullWhen(true)] string? tag, out Language language)
    {
        language = default;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var primary = tag.Trim().Split('-', '_')[0];
        if (primary.Equals("el", StringComparison.OrdinalIgnoreCase))
        {
            language = Language.El;
            return true;
        }

        if (primary.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            language = Language.En;
            return true;
        }

        return false;
    }
}

/// <summary>
/// A text in Greek and English (every UI string, message and template exists in both, contract §3.9.5).
/// JSON: <c>{"el": "…", "en": "…"}</c>.
/// </summary>
[JsonConverter(typeof(LocalizedTextJsonConverter))]
public sealed record LocalizedText
{
    /// <summary>Creates a text; both languages are required.</summary>
    public LocalizedText(string el, string en)
    {
        El = string.IsNullOrWhiteSpace(el) ? throw new ArgumentException("The Greek text is required.", nameof(el)) : el;
        En = string.IsNullOrWhiteSpace(en) ? throw new ArgumentException("The English text is required.", nameof(en)) : en;
    }

    /// <summary>Greek text.</summary>
    public string El { get; }

    /// <summary>English text.</summary>
    public string En { get; }

    /// <summary>The text in <paramref name="language"/>.</summary>
    public string In(Language language) => language == Language.En ? En : El;

    /// <summary>The Greek text (language of record).</summary>
    public override string ToString() => El;
}
