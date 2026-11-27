using CoreIns.Modules.Documents.Rendering;

namespace CoreIns.IntegrationTests.Documents;

/// <summary>BO-DOC-06: the payload dictionary and the template composer (pure, no database). Golden HTML is inline.</summary>
public sealed class TemplateComposerTests
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
    private static readonly IReadOnlyDictionary<string, ComposerClause> NoClauses = new Dictionary<string, ComposerClause>();

    private const string RefundBodyEl = "<p>Αγαπητέ/ή {{customerName}},</p><p>Θα επιστραφεί το ποσό {{refundAmount}} για το ασφαλιστήριο {{policyNo}}.</p>";

    private static Dictionary<string, string> RefundPayload() => new()
    {
        ["customerName"] = "Γιώργος <Παπαδόπουλος>", ["policyNo"] = "POL-1", ["refundAmount"] = "1234.5",
    };

    [Fact]
    public void Greek_refund_advice_matches_the_golden_html()
    {
        var result = TemplateComposer.Compose("DT-REFUND-ADVICE", "el", "Ενημέρωση επιστροφής χρημάτων", RefundBodyEl, RefundPayload(), NoClauses, Athens);

        result.IsSuccess.ShouldBeTrue(string.Join(";", result.Problems));
        result.Html.ShouldBe(
            "<!doctype html><html lang=\"el\"><head><meta charset=\"utf-8\"><title>Ενημέρωση επιστροφής χρημάτων</title>"
            + "<style>body{hyphens:auto}.nohyph{hyphens:none;white-space:nowrap}.clause h2{font-size:1.05em}</style></head>"
            + "<body><h1>Ενημέρωση επιστροφής χρημάτων</h1><p>Αγαπητέ/ή Γιώργος &lt;Παπαδόπουλος&gt;,</p>"
            + "<p>Θα επιστραφεί το ποσό 1.234,50\u00A0€ για το ασφαλιστήριο POL-1.</p></body></html>");
    }

    [Fact]
    public void English_dates_and_money_follow_the_legal_entity_zone()
    {
        const string body = "<p>{{customerName}} {{policyNo}} {{amountDue}} {{dueDate}}</p>";
        var payload = new Dictionary<string, string>
        {
            ["customerName"] = "A", ["policyNo"] = "P", ["amountDue"] = "10", ["dueDate"] = "2026-03-31T22:30:00Z",
        };

        var html = TemplateComposer.Compose("DT-NONPAY-NOTICE", "en", "T", body, payload, NoClauses, Athens).Html;

        // 22:30 UTC on 31 March is already 1 April in Athens (UTC+3 in summer time).
        html!.ShouldContain("<p>A P EUR\u00A010.00 01/04/2026</p>");
    }

    [Fact]
    public void Polytonic_text_becomes_monotonic()
    {
        TemplateComposer.Monotonic("Ἀθῆναι ὁδός ἐν ῳ").ShouldBe("Αθήναι οδός εν ω");
        TemplateComposer.Monotonic("plain text").ShouldBe("plain text");
    }

    [Fact]
    public void Afm_and_iban_values_are_validated_by_type()
    {
        PayloadDictionary.IsValid(PayloadFieldType.Afm, "123456789").ShouldBeTrue();
        PayloadDictionary.IsValid(PayloadFieldType.Afm, "12345").ShouldBeFalse();
        PayloadDictionary.IsValid(PayloadFieldType.Iban, "GR16 0110 1250 0000 0001 2300 695").ShouldBeTrue();
    }

    [Fact]
    public void An_undeclared_payload_field_is_refused()
    {
        var payload = RefundPayload();
        payload["secretField"] = "x";

        var result = TemplateComposer.Compose("DT-REFUND-ADVICE", "el", "T", RefundBodyEl, payload, NoClauses, Athens);

        result.IsSuccess.ShouldBeFalse();
        result.Problems.ShouldContain("Unknown payload field 'secretField'.");
    }

    [Fact]
    public void A_template_using_an_undeclared_placeholder_fails_closed()
    {
        var result = TemplateComposer.Compose("DT-REFUND-ADVICE", "el", "T", "<p>{{customerName}} {{iban}}</p>", RefundPayload(), NoClauses, Athens);

        result.IsSuccess.ShouldBeFalse();
        result.Problems.ShouldContain("The template uses the undeclared field 'iban'.");
    }

    [Fact]
    public void Missing_required_and_badly_typed_values_are_refused()
    {
        var payload = RefundPayload();
        payload.Remove("policyNo");
        payload["refundAmount"] = "lots";

        var problems = TemplateComposer.Compose("DT-REFUND-ADVICE", "el", "T", RefundBodyEl, payload, NoClauses, Athens).Problems;

        problems.ShouldContain("Missing required payload field 'policyNo'.");
        problems.ShouldContain("Payload field 'refundAmount' is not a valid money.");
    }

    [Fact]
    public void A_document_type_without_a_dictionary_fails_closed()
    {
        TemplateComposer.Compose("DT-UNKNOWN", "el", "T", "<p>x</p>", new Dictionary<string, string>(), NoClauses, Athens).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Approved_clauses_are_expanded_and_unknown_clauses_refuse()
    {
        var clauses = new Dictionary<string, ComposerClause> { ["CL-1"] = new("CL-1", "Τίτλος", "<p>Ρήτρα για {{policyNo}}</p>", "Title", "<p>Clause for {{policyNo}}</p>") };
        var payload = RefundPayload();
        const string body = "<p>{{refundAmount}}</p>{{clause:CL-1}}";

        var ok = TemplateComposer.Compose("DT-REFUND-ADVICE", "en", "T", body, payload, clauses, Athens);
        ok.Html!.ShouldContain("<div class=\"clause\"><h2>Title</h2><p>Clause for POL-1</p></div>");

        var missing = TemplateComposer.Compose("DT-REFUND-ADVICE", "en", "T", "{{clause:CL-9}}", payload, clauses, Athens);
        missing.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Dictionary_required_fields_equal_the_seeded_document_type_fields()
    {
        PayloadDictionary.FieldsOf("DT-NONPAY-NOTICE")!.Where(f => f.Required).Select(f => f.Name).ShouldBe(["customerName", "policyNo", "amountDue", "dueDate"]);
        PayloadDictionary.FieldsOf("DT-REFUND-ADVICE")!.Where(f => f.Required).Select(f => f.Name).ShouldBe(["customerName", "policyNo", "refundAmount"]);
    }
}
