using System.Globalization;
using System.Text.RegularExpressions;

namespace CoreIns.Modules.Documents.Rendering;

/// <summary>The value type of a declared payload field; it decides validation and formatting.</summary>
internal enum PayloadFieldType
{
    Text,
    Date,
    Money,
    Afm,
    Iban,
}

/// <summary>One declared payload field of a document type.</summary>
internal sealed record PayloadField(string Name, PayloadFieldType Type, bool Required);

/// <summary>
/// The payload dictionary (BO-DOC-06): the only fields a template of a document type may use, with their type and
/// whether they are required. Anything not declared here is refused, so a template or a caller can never pull in
/// data the dictionary does not know. A document type without a dictionary entry fails closed.
/// </summary>
internal static partial class PayloadDictionary
{
    private static readonly Dictionary<string, PayloadField[]> Types = new Dictionary<string, PayloadField[]>(StringComparer.Ordinal)
    {
        ["DT-NONPAY-NOTICE"] =
        [
            new("customerName", PayloadFieldType.Text, true), new("policyNo", PayloadFieldType.Text, true),
            new("amountDue", PayloadFieldType.Money, true), new("dueDate", PayloadFieldType.Date, true),
        ],
        ["DT-WITHDRAWAL-ACK"] =
        [
            new("customerName", PayloadFieldType.Text, true), new("policyNo", PayloadFieldType.Text, true), new("withdrawalDate", PayloadFieldType.Date, true),
        ],
        ["DT-CANCELLATION-NOTICE"] =
        [
            new("customerName", PayloadFieldType.Text, true), new("policyNo", PayloadFieldType.Text, true), new("effectiveDate", PayloadFieldType.Date, true),
        ],
        ["DT-REFUND-ADVICE"] =
        [
            new("customerName", PayloadFieldType.Text, true), new("policyNo", PayloadFieldType.Text, true), new("refundAmount", PayloadFieldType.Money, true),
        ],
    };

    /// <summary>The declared fields of a document type, or null when the type has no dictionary.</summary>
    public static IReadOnlyList<PayloadField>? FieldsOf(string documentType) => Types.TryGetValue(documentType, out var f) ? f : null;

    /// <summary>
    /// Problems with a payload: no dictionary, unknown keys, missing required fields, values that do not fit the declared type.
    /// An empty list means the payload is acceptable.
    /// </summary>
    public static IReadOnlyList<string> Validate(string documentType, IReadOnlyDictionary<string, string> payload)
    {
        var fields = FieldsOf(documentType);
        if (fields is null)
        {
            return [$"No payload dictionary for document type '{documentType}'."];
        }

        var problems = new List<string>();
        problems.AddRange(payload.Keys.Where(k => fields.All(f => f.Name != k)).OrderBy(k => k, StringComparer.Ordinal).Select(k => $"Unknown payload field '{k}'."));
        foreach (var field in fields)
        {
            payload.TryGetValue(field.Name, out var value);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required)
                {
                    problems.Add($"Missing required payload field '{field.Name}'.");
                }

                continue;
            }

            if (!IsValid(field.Type, value))
            {
                problems.Add($"Payload field '{field.Name}' is not a valid {field.Type.ToString().ToLowerInvariant()}.");
            }
        }

        return problems;
    }

    internal static bool IsValid(PayloadFieldType type, string value) => type switch
    {
        PayloadFieldType.Text => true,
        PayloadFieldType.Date => TryParseDate(value, TimeZoneInfo.Utc, out _),
        PayloadFieldType.Money => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
        PayloadFieldType.Afm => AfmPattern().IsMatch(value),
        PayloadFieldType.Iban => IbanPattern().IsMatch(value.Replace(" ", string.Empty, StringComparison.Ordinal)),
        _ => false,
    };

    /// <summary>A date is a plain ISO date or an ISO instant with offset; an instant is turned into the date in the legal entity's zone.</summary>
    internal static bool TryParseDate(string value, TimeZoneInfo zone, out DateOnly date)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        if (value.Length > 10 && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
            return true;
        }

        date = default;
        return false;
    }

    [GeneratedRegex(@"^\d{9}$")]
    private static partial Regex AfmPattern();

    [GeneratedRegex(@"^[A-Z]{2}\d{2}[A-Z0-9]{11,30}$")]
    private static partial Regex IbanPattern();
}
