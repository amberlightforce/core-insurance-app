using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreIns.Modules.Documents.Rendering;

/// <summary>A clause as the composer needs it: code and per-language text (HTML).</summary>
internal sealed record ComposerClause(string Code, string TitleEl, string TextEl, string TitleEn, string TextEn);

/// <summary>The composed HTML, or the reasons it could not be composed.</summary>
internal sealed record ComposedDocument(string? Html, IReadOnlyList<string> Problems)
{
    public bool IsSuccess => Html is not null;
}

/// <summary>
/// Composes a template body, its clauses and a payload into one HTML document (BO-DOC-06). Placeholders are
/// <c>{{field}}</c> and clauses are <c>{{clause:CODE}}</c>. It fails closed: a placeholder the dictionary does not declare,
/// an unknown or unapproved clause, or an invalid payload produces no HTML at all. Values are HTML-encoded and formatted by
/// their declared type (Greek or English numbers and dates, dates in the legal entity's zone); AFM and IBAN never hyphenate.
/// The composer is pure: it never reads or writes storage. It does not change the stored template text, so an approved
/// version always composes from exactly the words that were approved (only monotonic accents are normalised).
/// </summary>
internal static partial class TemplateComposer
{
    private const int MaxClauseDepth = 1;

    /// <summary>Composes one document. <paramref name="clauses"/> holds the APPROVED clauses by code.</summary>
    public static ComposedDocument Compose(
        string documentType, string language, string title, string body, IReadOnlyDictionary<string, string> payload, IReadOnlyDictionary<string, ComposerClause> clauses, TimeZoneInfo zone)
    {
        var el = language == "el";
        var problems = new List<string>(PayloadDictionary.Validate(documentType, payload));
        var fields = PayloadDictionary.FieldsOf(documentType) ?? [];

        var expanded = ExpandClauses(body, el, clauses, problems, 0);
        foreach (var name in PlaceholderPattern().Matches(expanded).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal))
        {
            if (fields.All(f => f.Name != name))
            {
                problems.Add($"The template uses the undeclared field '{name}'.");
            }
        }

        if (problems.Count > 0)
        {
            return new ComposedDocument(null, problems);
        }

        var text = Monotonic(expanded);
        var rendered = PlaceholderPattern().Replace(text, m =>
        {
            var field = fields.Single(f => f.Name == m.Groups[1].Value);
            return payload.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value) ? Format(field, value, el, zone) : string.Empty;
        });

        var encodedTitle = WebUtility.HtmlEncode(Monotonic(title));
        var html = $"<!doctype html><html lang=\"{language}\"><head><meta charset=\"utf-8\"><title>{encodedTitle}</title>"
            + "<style>body{hyphens:auto}.nohyph{hyphens:none;white-space:nowrap}.clause h2{font-size:1.05em}</style>"
            + $"</head><body><h1>{encodedTitle}</h1>{rendered}</body></html>";
        return new ComposedDocument(html, []);
    }

    private static string ExpandClauses(string text, bool el, IReadOnlyDictionary<string, ComposerClause> clauses, List<string> problems, int depth) =>
        ClausePattern().Replace(text, m =>
        {
            var code = m.Groups[1].Value;
            if (!clauses.TryGetValue(code, out var clause))
            {
                problems.Add($"The clause '{code}' does not exist or is not approved.");
                return string.Empty;
            }

            var clauseTitle = el ? clause.TitleEl : clause.TitleEn;
            var clauseText = el ? clause.TextEl : clause.TextEn;
            if (depth < MaxClauseDepth)
            {
                clauseText = ExpandClauses(clauseText, el, clauses, problems, depth + 1);
            }
            else if (ClausePattern().IsMatch(clauseText))
            {
                problems.Add($"The clause '{code}' nests clauses too deeply.");
            }

            return $"<div class=\"clause\"><h2>{WebUtility.HtmlEncode(clauseTitle)}</h2>{clauseText}</div>";
        });

    private static string Format(PayloadField field, string value, bool el, TimeZoneInfo zone)
    {
        switch (field.Type)
        {
            case PayloadFieldType.Date when PayloadDictionary.TryParseDate(value, zone, out var date):
                return date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            case PayloadFieldType.Money when decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount):
                return FormatMoney(amount, el);
            case PayloadFieldType.Afm:
            case PayloadFieldType.Iban:
                return $"<span class=\"nohyph\">{WebUtility.HtmlEncode(value)}</span>";
            default:
                return WebUtility.HtmlEncode(value);
        }
    }

    /// <summary>Greek: <c>1.234,56 €</c>; English: <c>EUR 1,234.56</c>.</summary>
    internal static string FormatMoney(decimal amount, bool el)
    {
        var plain = Math.Abs(amount).ToString("#,##0.00", CultureInfo.InvariantCulture);
        var sign = amount < 0 ? "-" : string.Empty;
        return el
            ? sign + plain.Replace(',', '\u0001').Replace('.', ',').Replace('\u0001', '.') + " €"
            : $"{sign}EUR {plain}";
    }

    /// <summary>Monotonic Greek: drops breathings and iota subscript, turns grave and circumflex into acute.</summary>
    internal static string Monotonic(string text)
    {
        if (!text.Any(c => c is >= 'Ͱ' and <= 'Ͽ' or >= 'ἀ' and <= '῿'))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            switch ((int)c)
            {
                case 0x0313 or 0x0314 or 0x0345:
                    break;
                case 0x0300 or 0x0342:
                    sb.Append((char)0x0301);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\{\{clause:([A-Za-z0-9_.\-]{1,64})\}\}")]
    private static partial Regex ClausePattern();

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]{0,63})\}\}")]
    private static partial Regex PlaceholderPattern();
}
