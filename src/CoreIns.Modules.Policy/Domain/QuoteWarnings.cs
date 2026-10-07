using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Domain;

/// <summary>
/// Non-blocking warnings of a quote: the codes RAT and UW return are passed through unchanged with a text in the request
/// language (el default, en). Unknown codes keep the code as their text, so nothing a producer adds is lost.
/// </summary>
internal static class QuoteWarnings
{
    private static readonly Dictionary<string, (string El, string En)> Texts = new(StringComparer.Ordinal)
    {
        ["RAT-WARN-ILLUSTRATIVE-TARIFF"] = (
            "Οι τιμές είναι δοκιμαστικά δεδομένα και όχι εγκεκριμένο τιμολόγιο.",
            "The rates are illustrative test data, not an approved tariff."),
        ["RAT-WARN-PROVISIONAL-TAX"] = (
            "Ο φόρος ή η εισφορά βασίζεται σε τιμή που δεν έχει οριστικοποιηθεί νομικά (προσωρινή).",
            "A tax or levy rests on a value whose legal status is not Settled (provisional)."),
        ["UW-WARN-ILLUSTRATIVE-RULES"] = (
            "Οι κανόνες αναδοχής είναι δοκιμαστικά δεδομένα.",
            "The underwriting rules are illustrative test data."),
    };

    public static List<JobQuoteResponse.WarningItem> From(IEnumerable<string?> codes, Language language) =>
    [
        .. codes.Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).Distinct(StringComparer.Ordinal).Select(code => new JobQuoteResponse.WarningItem
        {
            Code = code,
            Message = Texts.TryGetValue(code, out var text) ? (language == Language.En ? text.En : text.El) : code,
        }),
    ];
}
