using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PdfaSpike;

/// <summary>Frozen payload for a Greek motor policy schedule (stand-in for the DOC payload builder).</summary>
public sealed record SchedulePayload(
    string DocumentNumber,
    string ReferenceTimeUtc,          // payload reference time -> fixed /CreationDate (REQ-DOC-160)
    string PolicyNumber,
    string Insured,
    string InsuredLatin,
    string Address,
    string Afm,
    string Plate,
    string Vehicle,
    string PeriodFrom,
    string PeriodTo,
    string Intermediary,
    IReadOnlyList<CoverRow> Covers,
    decimal NetPremium,
    decimal Levies,
    decimal StampDuty,
    decimal Total,
    string ExtraText);

public sealed record CoverRow(string Code, string Name, decimal SumInsured, decimal Deductible, decimal Premium);

/// <summary>
/// Minimal stand-in for the in-house template-tree -> HTML composer (D-ARC-07).
/// Formatting is done here, deterministically, with an explicit el-GR number format
/// (no dependency on the host ICU/CLDR version).
/// </summary>
public static class Composer
{
    static readonly NumberFormatInfo ElGr = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = [3],
    };

    /// <summary>"1.234,56 €" (el-GR: decimal comma, thousands dot, euro after with a NBSP).</summary>
    public static string Eur(decimal v) => v.ToString("#,##0.00", ElGr) + " €";

    static string H(string s) => WebUtility.HtmlEncode(s).Replace("&#171;", "«").Replace("&#187;", "»");

    public static SchedulePayload SamplePayload(int coverRows, string extraText = "")
    {
        var names = new[]
        {
            "Αστική Ευθύνη έναντι Τρίτων (σωματικές βλάβες)",
            "Αστική Ευθύνη έναντι Τρίτων (υλικές ζημιές)",
            "Νομική Προστασία",
            "Οδική Βοήθεια «Πανελλαδική»",
            "Θραύση Κρυστάλλων",
            "Πυρός & Ολικής Κλοπής",
            "Ίδιες Ζημιές από Σύγκρουση",
            "Φυσικά Φαινόμενα (πλημμύρα, χαλάζι, σεισμός)",
            "Τρομοκρατικές Ενέργειες – Στάσεις – Απεργίες",
            "Προσωπικό Ατύχημα Οδηγού",
        };
        var covers = new List<CoverRow>(coverRows);
        decimal net = 0;
        for (int i = 0; i < coverRows; i++)
        {
            var prem = 12.34m + (i * 7.17m) % 400m;
            covers.Add(new CoverRow($"K{i + 1:0000}", names[i % names.Length], 1_234.56m * (1 + i % 13) * 100, (i % 4) * 150m, prem));
            net += prem;
        }
        var levies = Math.Round(net * 0.15m, 2);
        var stamp = Math.Round(net * 0.024m, 2);
        return new SchedulePayload(
            DocumentNumber: "DOC-2026-000123457",
            ReferenceTimeUtc: "2026-10-01T09:30:00Z",
            PolicyNumber: "ΑΥΤ-2026-0001234",
            Insured: "Παπαδοπούλου Αικατερίνη-Ελισάβετ",
            InsuredLatin: "PAPADOPOULOU AIKATERINI-ELISAVET",
            Address: "Λεωφόρος Βασιλίσσης Σοφίας 123, 115 21 Αθήνα",
            Afm: "123456789",
            Plate: "ΙΚΧ-1234",
            Vehicle: "Toyota Yaris Hybrid 1.5, 2023, 116 PS",
            PeriodFrom: "01/10/2026",
            PeriodTo: "01/10/2027",
            Intermediary: "Ασφαλιστική Πράκτορας Ιωάννης Κωνσταντίνου, ΑΦΜ 987654321, Αρ. ΓΕΜΗ 112233445000",
            Covers: covers,
            NetPremium: net,
            Levies: levies,
            StampDuty: stamp,
            Total: net + levies + stamp,
            ExtraText: extraText);
    }

    /// <summary>Canonical JSON (stable property order, no whitespace) - the bytes we embed and hash.</summary>
    public static byte[] CanonicalJson(SchedulePayload p) =>
        JsonSerializer.SerializeToUtf8Bytes(p, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    public const string FontCss = """
        @font-face { font-family: 'DocSans'; src: url('NotoSans-Regular.ttf') format('truetype'); font-weight: 400; font-style: normal; }
        @font-face { font-family: 'DocSans'; src: url('NotoSans-Bold.ttf') format('truetype'); font-weight: 700; font-style: normal; }
        """;

    public static string Index(SchedulePayload p, bool marginBoxes = true)
    {
        var sb = new StringBuilder(64 * 1024 + p.Covers.Count * 400);
        sb.Append("""
            <!DOCTYPE html>
            <html lang="el">
            <head>
            <meta charset="utf-8">
            <title>Πίνακας Ασφάλισης Οχήματος</title>
            <style>
            """);
        sb.Append(FontCss);
        sb.Append("""
            @page { size: A4; margin: 28mm 16mm 22mm 16mm; }
            """);
        if (marginBoxes)
            sb.Append("""
            @page {
              @top-left { content: "Ελληνική Ασφαλιστική Α.Ε. · Πίνακας Ασφάλισης Οχήματος"; font-family: 'DocSans'; font-size: 8pt; }
              @bottom-center { content: "Σελίδα " counter(page) " από " counter(pages); font-family: 'DocSans'; font-size: 8pt; }
            }
            """);
        sb.Append("""
            html, body { font-family: 'DocSans'; font-size: 9.5pt; color: #000; }
            h1 { font-size: 15pt; margin: 0 0 2mm 0; }
            h2 { font-size: 11pt; margin: 5mm 0 2mm 0; }
            table { width: 100%; border-collapse: collapse; }
            thead { display: table-header-group; }
            tr { break-inside: avoid; }
            th, td { border-bottom: 0.5pt solid #555; padding: 1.2mm 1.5mm; text-align: left; vertical-align: top; }
            td.num, th.num { text-align: right; white-space: nowrap; }
            dl { display: grid; grid-template-columns: 45mm 1fr; gap: 1mm 4mm; margin: 0; }
            dt { font-weight: 700; } dd { margin: 0; }
            .totals td { font-weight: 700; }
            p.note { font-size: 8.5pt; }
            </style>
            </head>
            <body>
            <main>
            """);
        // Upper-case title produced by the composer from the language rule (el-Upper strips tonos)
        sb.Append("<h1>ΑΣΦΑΛΙΣΤΗΡΙΟ ΟΧΗΜΑΤΟΣ – ΠΙΝΑΚΑΣ ΑΣΦΑΛΙΣΗΣ</h1>\n");
        sb.Append($"<p>Αριθμός εγγράφου {H(p.DocumentNumber)} · Ασφαλιστήριο «{H(p.PolicyNumber)}»</p>\n");
        sb.Append("<h2>Στοιχεία Συμβολαίου</h2>\n<dl>\n");
        void Row(string k, string v) => sb.Append($"<dt>{H(k)}</dt><dd>{H(v)}</dd>\n");
        Row("Λήπτης / Ασφαλισμένος", p.Insured);
        Row("Ονοματεπώνυμο (λατινικά)", p.InsuredLatin);
        Row("Διεύθυνση", p.Address);
        Row("ΑΦΜ", p.Afm);
        Row("Αριθμός κυκλοφορίας", p.Plate);
        Row("Όχημα", p.Vehicle);
        Row("Διάρκεια", $"από {p.PeriodFrom} έως {p.PeriodTo}");
        Row("Διαμεσολαβητής", p.Intermediary);
        sb.Append("</dl>\n");
        sb.Append("<h2>Καλύψεις</h2>\n<table>\n<thead><tr><th scope=\"col\">Κωδ.</th><th scope=\"col\">Κάλυψη</th><th scope=\"col\" class=\"num\">Ασφαλισμένο κεφάλαιο</th><th scope=\"col\" class=\"num\">Απαλλαγή</th><th scope=\"col\" class=\"num\">Ασφάλιστρο</th></tr></thead>\n<tbody>\n");
        foreach (var c in p.Covers)
            sb.Append($"<tr><td>{c.Code}</td><td>{H(c.Name)}</td><td class=\"num\">{Eur(c.SumInsured)}</td><td class=\"num\">{Eur(c.Deductible)}</td><td class=\"num\">{Eur(c.Premium)}</td></tr>\n");
        sb.Append("</tbody>\n</table>\n");
        sb.Append("<h2>Ανάλυση Ασφαλίστρου</h2>\n<table class=\"totals\"><tbody>\n");
        sb.Append($"<tr><th scope=\"row\">Καθαρό ασφάλιστρο</th><td class=\"num\">{Eur(p.NetPremium)}</td></tr>\n");
        sb.Append($"<tr><th scope=\"row\">Φόροι και εισφορές</th><td class=\"num\">{Eur(p.Levies)}</td></tr>\n");
        sb.Append($"<tr><th scope=\"row\">Χαρτόσημο</th><td class=\"num\">{Eur(p.StampDuty)}</td></tr>\n");
        sb.Append($"<tr><th scope=\"row\">Συνολικό πληρωτέο</th><td class=\"num\">{Eur(p.Total)}</td></tr>\n");
        sb.Append("</tbody></table>\n");
        sb.Append("""
            <h2>Σημαντικές Πληροφορίες</h2>
            <p class="note">Το παρόν αποτελεί απόδειξη της σύμβασης ασφάλισης. Η ελληνική έκδοση είναι η δεσμευτική.
            Ο λήπτης της ασφάλισης έχει δικαίωμα εναντίωσης εντός δεκατεσσάρων (14) ημερών από την παραλαβή του
            ασφαλιστηρίου. Τελικό σίγμα: «ζημιάς», «οδηγός», «κινδύνους». Διαλυτικά: Ευρωπαϊκή, προϋπόθεση, ΑΫΠΝΙΑ.
            Πολυτονικό δείγμα: Ἡ ἀσφάλισις ἰσχύει ἐφ᾽ ὅσον καταβληθῇ τὸ ἀσφάλιστρον.</p>
            """);
        if (!string.IsNullOrEmpty(p.ExtraText))
            sb.Append($"<p class=\"note\">{H(p.ExtraText)}</p>\n");
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    // Chromium header/footer templates: separate documents; cannot fetch url() resources,
    // so the font is inlined as a data: URI (declared font only -> no fallback).
    public static string Header(string fontDataUri) => $$"""
        <!DOCTYPE html><html lang="el"><head><meta charset="utf-8"><style>
        @font-face { font-family: 'DocSans'; src: url('{{fontDataUri}}') format('truetype'); }
        body { font-family: 'DocSans'; font-size: 8pt; margin: 0 16mm; width: 100%; }
        </style></head><body><div style="font-family:'DocSans';font-size:8pt;width:100%;padding:0 16mm;">
        Ελληνική Ασφαλιστική Α.Ε. · Πίνακας Ασφάλισης Οχήματος
        </div></body></html>
        """;

    public static string Footer(string fontDataUri) => $$"""
        <!DOCTYPE html><html lang="el"><head><meta charset="utf-8"><style>
        @font-face { font-family: 'DocSans'; src: url('{{fontDataUri}}') format('truetype'); }
        body { font-family: 'DocSans'; font-size: 8pt; margin: 0; }
        </style></head><body><div style="font-family:'DocSans';font-size:8pt;width:100%;text-align:center;">
        Σελίδα <span class="pageNumber"></span> από <span class="totalPages"></span>
        </div></body></html>
        """;
}
