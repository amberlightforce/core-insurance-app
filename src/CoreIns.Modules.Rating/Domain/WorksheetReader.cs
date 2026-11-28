using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Rating.Domain;

/// <summary>The outcome of a reperformance (REQ-RAT-003): whether the stored worksheet was reproduced bit for bit, and where not.</summary>
internal sealed record ReperformResult(JsonObject Reproduced, string StoredHash, string ReperformedHash, bool StoredIntact, IReadOnlyList<JsonObject> Drift)
{
    public bool Match => Drift.Count == 0 && StoredIntact && string.Equals(StoredHash, ReperformedHash, StringComparison.Ordinal);
}

/// <summary>
/// The read side of a stored worksheet (BO-RAT-02): plain-language explanation from the step descriptions, CSV export, and
/// reperformance under the hashes the worksheet pinned. Pure: no I/O, so the same worksheet always explains and exports the same.
/// </summary>
internal static class WorksheetReader
{
    private const int MaxDrift = 100;

    public static readonly string[] CsvColumns =
    [
        "section", "segmentId", "coverageCode", "chargeType", "step", "kind", "table", "rule", "value", "before", "after", "applied", "base", "rate",
        "amount", "explanation",
    ];

    /// <summary>The supported languages are Greek and English; anything else answers in English.</summary>
    public static string Language(string? language) =>
        language is not null && language.StartsWith("el", StringComparison.OrdinalIgnoreCase) ? "el" : "en";

    /// <summary>One worksheet line of one coverage (also used when the worksheet is first built, so rating and reperformance cannot diverge).</summary>
    public static JsonObject LineJson(CompiledArtefact artefact, string segmentId, MotorRisk risk, CoveragePremium premium) => new()
    {
        ["segmentId"] = segmentId,
        ["elementId"] = risk.VehicleElementId,
        ["coverageCode"] = premium.Coverage,
        ["chargeType"] = artefact.Definition.ChargeTypes[premium.Coverage],
        ["annualPremium"] = RatingEngine.Format(premium.Premium),
        ["steps"] = RatingEngine.TraceJson(premium.Steps),
    };

    // ---- explain -------------------------------------------------------------------------------------------------------------------------

    public static JsonObject Explain(JsonObject worksheet, string language)
    {
        var header = worksheet["header"] as JsonObject ?? new JsonObject();
        var el = language == "el";
        var lines = new JsonArray();
        foreach (var line in Items(worksheet, "lines"))
        {
            var steps = new JsonArray();
            foreach (var step in Items(line, "steps"))
            {
                steps.Add(new JsonObject
                {
                    ["step"] = Text(step, "step"),
                    ["kind"] = Text(step, "kind"),
                    ["applied"] = step?["applied"]?.GetValue<bool>() ?? false,
                    ["before"] = Text(step, "before"),
                    ["after"] = Text(step, "after"),
                    ["text"] = StepText(step, language),
                });
            }

            lines.Add(new JsonObject
            {
                ["segmentId"] = Text(line, "segmentId"),
                ["coverageCode"] = Text(line, "coverageCode"),
                ["chargeType"] = Text(line, "chargeType"),
                ["annualPremium"] = Text(line, "annualPremium"),
                ["steps"] = steps,
            });
        }

        var taxes = new JsonArray();
        foreach (var tax in Items(worksheet, "taxes"))
        {
            var text = el
                ? $"{Text(tax, "category") switch { "LEVY" => "Εισφορά", _ => "Φόρος" }} {Text(tax, "chargeType")}: {Text(tax, "rate")} x {Text(tax, "base")} = {Text(tax, "amount")} (κατηγορία {Text(tax, "taxClass")}, στρογγυλοποίηση {Text(tax, "rounding")})"
                : $"{Text(tax, "category") switch { "LEVY" => "Levy", _ => "Tax" }} {Text(tax, "chargeType")}: {Text(tax, "rate")} x {Text(tax, "base")} = {Text(tax, "amount")} (class {Text(tax, "taxClass")}, rounding {Text(tax, "rounding")})";
            taxes.Add(new JsonObject
            {
                ["segmentId"] = Text(tax, "segmentId"),
                ["coverageCode"] = Text(tax, "coverageCode"),
                ["chargeType"] = Text(tax, "chargeType"),
                ["amount"] = Text(tax, "amount"),
                ["provisional"] = tax?["provisional"]?.GetValue<bool>() ?? false,
                ["text"] = text,
            });
        }

        var totals = worksheet["totals"] as JsonObject;
        return new JsonObject
        {
            ["language"] = language,
            ["summary"] = new JsonObject
            {
                ["productCode"] = Text(header, "productCode"),
                ["productVersion"] = Text(header, "productVersion"),
                ["ratingArtefactHash"] = Text(header, "ratingArtefactHash"),
                ["configurationHash"] = Text(header, "configurationHash"),
                ["currency"] = Text(header, "currency"),
                ["ratingBasisDate"] = Text(header, "ratingBasisDate"),
                ["dataStatus"] = Text(header, "dataStatus"),
            },
            ["notice"] = Text(header, "notice"),
            ["lines"] = lines,
            ["taxes"] = taxes,
            ["totals"] = totals?.DeepClone(),
        };
    }

    private static string StepText(JsonNode? step, string language)
    {
        var explanation = step?["explanation"]?[language]?.GetValue<string>() ?? step?["explanation"]?["en"]?.GetValue<string>() ?? Text(step, "step");
        var applied = step?["applied"]?.GetValue<bool>() ?? false;
        if (!applied)
        {
            return language == "el" ? $"{explanation} (δεν εφαρμόστηκε)" : $"{explanation} (not applied)";
        }

        var value = Text(step, "value");
        var lookup = value.Length == 0 ? string.Empty : language == "el" ? $" τιμή {value}:" : $" value {value}:";
        return $"{explanation}{lookup} {Text(step, "before")} -> {Text(step, "after")}";
    }

    // ---- export --------------------------------------------------------------------------------------------------------------------------

    /// <summary>The worksheet as CSV (RFC 4180, CRLF), one row per line, step, tax and total; text cells starting with a formula character are neutralised.</summary>
    public static string ToCsv(JsonObject worksheet, string language)
    {
        var builder = new StringBuilder();
        Row(builder, CsvColumns);
        foreach (var line in Items(worksheet, "lines"))
        {
            var segment = Text(line, "segmentId");
            var coverage = Text(line, "coverageCode");
            var charge = Text(line, "chargeType");
            Row(builder, ["LINE", segment, coverage, charge, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Text(line, "annualPremium"), string.Empty]);
            foreach (var step in Items(line, "steps"))
            {
                Row(builder,
                [
                    "STEP", segment, coverage, charge, Text(step, "step"), Text(step, "kind"), Text(step, "table"), Text(step, "rule"), Text(step, "value"),
                    Text(step, "before"), Text(step, "after"), Text(step, "applied").ToLowerInvariant(), string.Empty, string.Empty, string.Empty, StepText(step, language),
                ]);
            }
        }

        foreach (var tax in Items(worksheet, "taxes"))
        {
            Row(builder,
            [
                "TAX", Text(tax, "segmentId"), Text(tax, "coverageCode"), Text(tax, "chargeType"), string.Empty, Text(tax, "category"), string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, Text(tax, "base"), Text(tax, "rate"), Text(tax, "amount"), Text(tax, "taxClass"),
            ]);
        }

        if (worksheet["totals"] is JsonObject totals)
        {
            foreach (var (key, label) in new[] { ("coveragePremium", "COVERAGE-PREMIUM"), ("tax", "TAX"), ("gross", "GROSS") })
            {
                Row(builder, ["TOTAL", string.Empty, string.Empty, label, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Text(totals, key), string.Empty]);
            }
        }

        return builder.ToString();
    }

    private static void Row(StringBuilder builder, string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var cell = Neutralise(cells[i]);
            if (cell.AsSpan().IndexOfAny(",\"\r\n") >= 0)
            {
                builder.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                builder.Append(cell);
            }
        }

        builder.Append("\r\n");
    }

    /// <summary>Spreadsheet formula injection: a text cell must not start a formula. Plain numbers (including "-") are left alone.</summary>
    private static string Neutralise(string cell)
    {
        if (cell.Length == 0)
        {
            return cell;
        }

        var first = cell[0];
        var numeric = decimal.TryParse(cell, NumberStyles.Number, CultureInfo.InvariantCulture, out _) || cell == "-";
        return !numeric && first is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + cell : cell;
    }

    // ---- reperform -----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Re-rates the stored inputs under the artefact the worksheet pinned (loaded by its hash by the caller, never the active one) and with
    /// the tax rates the worksheet recorded (the configuration it was priced under), then compares the result with the stored worksheet.
    /// </summary>
    public static ReperformResult Reperform(JsonObject stored, string worksheetId, CompiledArtefact artefact)
    {
        var storedHash = CanonicalJson.Hash(stored).Value;
        var storedIntact = string.Equals(storedHash, worksheetId, StringComparison.Ordinal);
        var header = stored["header"] as JsonObject ?? throw Unavailable("The worksheet has no header.");
        var inputs = stored["inputs"] as JsonArray ?? throw Unavailable("The worksheet was stored before inputs were kept and cannot be reperformed.");
        var basis = DateOnly.ParseExact(Text(header, "ratingBasisDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var currency = Currency.FromCode(Text(header, "currency"));

        var reproduced = (JsonObject)stored.DeepClone();
        var drift = new List<JsonObject>();
        try
        {
            var lines = new JsonArray();
            var premiums = new Dictionary<(string Segment, string Coverage), decimal>();
            var rebuiltInputs = new JsonArray();
            foreach (var input in inputs)
            {
                var segmentId = Text(input, "segmentId");
                var risk = MotorRisk.FromNormalised(input?["risk"] ?? throw Unavailable("A stored input has no risk."), basis);
                rebuiltInputs.Add(new JsonObject { ["segmentId"] = segmentId, ["risk"] = risk.ToNormalised(basis) });
                foreach (var coverage in risk.Coverages)
                {
                    var premium = RatingEngine.RateCoverage(artefact, risk, coverage, basis);
                    premiums[(segmentId, coverage)] = premium.Premium;
                    lines.Add(LineJson(artefact, segmentId, risk, premium));
                }
            }

            reproduced["inputs"] = rebuiltInputs;
            ((JsonObject)reproduced["header"]!)["inputHash"] = CanonicalJson.Hash(rebuiltInputs.DeepClone()).Value;
            reproduced["lines"] = lines;

            var taxTotal = new List<Money>();
            foreach (var tax in Items(reproduced, "taxes").OfType<JsonObject>())
            {
                var key = (Text(tax, "segmentId"), Text(tax, "coverageCode"));
                var plan = artefact.Definition.TaxPlan.FirstOrDefault(p => p.ChargeType == Text(tax, "chargeType"));
                if (plan is null || !premiums.TryGetValue(key, out var premium))
                {
                    Add(drift, $"taxes[{key.Item1}/{key.Item2}/{Text(tax, "chargeType")}]", "present", "not produced by the pinned artefact");
                    taxTotal.Add(new Money(ParseDecimal(Text(tax, "amount")), currency));
                    continue;
                }

                var baseAmount = new Money(premium, currency);
                var amount = RatingEngine.TaxAmount(baseAmount, ParseDecimal(Text(tax, "rate")), plan);
                tax["base"] = RatingEngine.Format(baseAmount.Amount);
                tax["amount"] = RatingEngine.Format(amount.Amount);
                taxTotal.Add(amount);
            }

            var premiumTotal = Money.Sum(lines.OfType<JsonObject>().Select(l => new Money(ParseDecimal(Text(l, "annualPremium")), currency)), currency);
            var tax1 = Money.Sum(taxTotal, currency);
            reproduced["totals"] = new JsonObject
            {
                ["coveragePremium"] = RatingEngine.Format(premiumTotal.Amount),
                ["tax"] = RatingEngine.Format(tax1.Amount),
                ["gross"] = RatingEngine.Format((premiumTotal + tax1).Amount),
            };
        }
        catch (DomainException ex)
        {
            Add(drift, "$", "reproducible", $"{ex.Error.Code}: {ex.Error.Detail}");
        }

        Diff(stored, reproduced, "$", drift);
        return new ReperformResult(reproduced, storedHash, CanonicalJson.Hash(reproduced).Value, storedIntact, drift);
    }

    private static void Diff(JsonNode? stored, JsonNode? reproduced, string path, List<JsonObject> drift)
    {
        if (drift.Count >= MaxDrift)
        {
            return;
        }

        switch (stored, reproduced)
        {
            case (JsonObject a, JsonObject b):
                foreach (var name in a.Select(p => p.Key).Union(b.Select(p => p.Key)).Order(StringComparer.Ordinal))
                {
                    Diff(a[name], b[name], $"{path}.{name}", drift);
                }

                break;
            case (JsonArray a, JsonArray b):
                for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
                {
                    Diff(i < a.Count ? a[i] : null, i < b.Count ? b[i] : null, $"{path}[{i}]", drift);
                }

                break;
            default:
                if (!JsonNode.DeepEquals(stored, reproduced))
                {
                    Add(drift, path, stored?.ToJsonString() ?? "(absent)", reproduced?.ToJsonString() ?? "(absent)");
                }

                break;
        }
    }

    private static void Add(List<JsonObject> drift, string path, string stored, string reperformed)
    {
        if (drift.Count < MaxDrift)
        {
            drift.Add(new JsonObject { ["path"] = path, ["stored"] = stored, ["reperformed"] = reperformed });
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------

    private static JsonArray Items(JsonNode? node, string name) => node?[name] as JsonArray ?? [];

    private static string Text(JsonNode? node, string name)
    {
        var value = node?[name];
        return value is null ? string.Empty : value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
    }

    private static decimal ParseDecimal(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : throw Unavailable($"'{text}' is not an amount.");

    private static DomainException Unavailable(string detail) => new(DomainError.Of(ModuleCode.RAT, "DOMAIN", detail));
}
