using System.Net;
using System.Text;

namespace CoreIns.Modules.Documents.Domain;

/// <summary>Delivery media and channels of the stub providers (D-SL6-11, D-SL6-12).</summary>
internal static class Media
{
    public const string Email = "EMAIL";
    public const string RegisteredPost = "REGISTERED_POST";

    /// <summary>Stub provider codes accepted by <c>doc.ProviderEvent.receive</c> (dev and test only, never in Production).</summary>
    public const string StubPost = "stub-post";
    public const string StubEmail = "stub-email";

    public static bool IsKnown(string medium) => medium is Email or RegisteredPost;

    public static string ChannelFor(string medium) => medium == RegisteredPost ? StubPost : StubEmail;

    public static bool IsStubProvider(string provider) => provider is StubPost or StubEmail;
}

/// <summary>A letter template: HTML body per language with <c>{{placeholder}}</c> markers.</summary>
internal sealed record LetterTemplate(string Key, string DefaultMedium, string[] Required, string TitleEl, string BodyEl, string TitleEn, string BodyEn);

/// <summary>The four SL6 templates (D-SL6-12): HTML, el and en. Texts are illustrative drafts, not approved legal wording.</summary>
internal static class Templates
{
    public const string NonPayNotice = "DT-NONPAY-NOTICE";
    public const string WithdrawalAck = "DT-WITHDRAWAL-ACK";
    public const string CancellationNotice = "DT-CANCELLATION-NOTICE";
    public const string RefundAdvice = "DT-REFUND-ADVICE";

    private static readonly LetterTemplate[] All =
    [
        new(NonPayNotice, Media.RegisteredPost, ["customerName", "policyNo", "amountDue", "dueDate"],
            "Ειδοποίηση μη πληρωμής ασφαλίστρου",
            "<p>Αγαπητέ/ή {{customerName}},</p><p>Για το ασφαλιστήριο {{policyNo}} δεν έχει εξοφληθεί το ποσό {{amountDue}} που έληγε στις {{dueDate}}. "
            + "Εάν δεν εξοφληθεί εντός της νόμιμης προθεσμίας από την κοινοποίηση της παρούσας, η ασφάλιση καταγγέλλεται.</p>",
            "Notice of non-payment of premium",
            "<p>Dear {{customerName}},</p><p>For policy {{policyNo}} the amount {{amountDue}} due on {{dueDate}} has not been paid. "
            + "If it is not paid within the statutory period from notification of this letter, the cover will be cancelled.</p>"),
        new(WithdrawalAck, Media.Email, ["customerName", "policyNo", "withdrawalDate"],
            "Επιβεβαίωση υπαναχώρησης",
            "<p>Αγαπητέ/ή {{customerName}},</p><p>Παραλάβαμε τη δήλωση υπαναχώρησής σας για το ασφαλιστήριο {{policyNo}} στις {{withdrawalDate}}.</p>",
            "Withdrawal acknowledgement",
            "<p>Dear {{customerName}},</p><p>We received your withdrawal from policy {{policyNo}} on {{withdrawalDate}}.</p>"),
        new(CancellationNotice, Media.Email, ["customerName", "policyNo", "effectiveDate"],
            "Ειδοποίηση ακύρωσης ασφαλιστηρίου",
            "<p>Αγαπητέ/ή {{customerName}},</p><p>Το ασφαλιστήριο {{policyNo}} ακυρώνεται με ισχύ από {{effectiveDate}}.</p>",
            "Notice of policy cancellation",
            "<p>Dear {{customerName}},</p><p>Policy {{policyNo}} is cancelled with effect from {{effectiveDate}}.</p>"),
        new(RefundAdvice, Media.Email, ["customerName", "policyNo", "refundAmount"],
            "Ενημέρωση επιστροφής χρημάτων",
            "<p>Αγαπητέ/ή {{customerName}},</p><p>Θα επιστραφεί το ποσό {{refundAmount}} για το ασφαλιστήριο {{policyNo}}.</p>",
            "Refund advice",
            "<p>Dear {{customerName}},</p><p>The amount {{refundAmount}} will be refunded for policy {{policyNo}}.</p>"),
    ];

    public static LetterTemplate? Find(string key) => Array.Find(All, t => string.Equals(t.Key, key, StringComparison.Ordinal));

    public static bool IsLanguage(string language) => language is "el" or "en";

    /// <summary>Missing required placeholders, if any (placeholders come from the request payload only).</summary>
    public static IReadOnlyList<string> Missing(LetterTemplate template, IReadOnlyDictionary<string, string> values) =>
        [.. template.Required.Where(r => !values.TryGetValue(r, out var v) || string.IsNullOrWhiteSpace(v))];

    /// <summary>Renders the HTML document. Values are HTML-encoded; unknown placeholders are left out.</summary>
    public static string Render(LetterTemplate template, string language, IReadOnlyDictionary<string, string> values)
    {
        var el = language == "el";
        var title = el ? template.TitleEl : template.TitleEn;
        var body = el ? template.BodyEl : template.BodyEn;
        var sb = new StringBuilder(body);
        foreach (var (name, value) in values)
        {
            sb.Replace("{{" + name + "}}", WebUtility.HtmlEncode(value));
        }

        return $"<!doctype html><html lang=\"{language}\"><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title></head><body><h1>{WebUtility.HtmlEncode(title)}</h1>{sb}</body></html>";
    }
}

/// <summary>
/// Statutory medium rule (D-SL6-03, OI-DOC-03): a non-payment notice goes by registered post only, because only a
/// registered-post proof starts the statutory waiting period. Electronic proof is not accepted for it.
/// </summary>
internal static class StatutoryDeliveryRule
{
    public static string MediumFor(LetterTemplate template, string? requested) =>
        template.Key == Templates.NonPayNotice ? Media.RegisteredPost : requested ?? template.DefaultMedium;

    /// <summary>The proof level a DELIVERED callback gives on the medium.</summary>
    public static string ProofLevel(string medium) => medium == Media.RegisteredPost ? "REGISTERED_POST_PROOF" : "PROVIDER_ACK";
}
