using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;

namespace CoreIns.IntegrationTests.Rating;

/// <summary>
/// The pure rating function on the shared rule engine, without a database (W3-RAT-01/02/03 subset). All tables are
/// illustrative test data (D-SLC-04): the golden numbers below pin the engine's arithmetic, not a tariff.
/// </summary>
public sealed class RatingEngineTests
{
    private static readonly DateOnly Basis = new(2026, 11, 1);

    private static decimal D(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static CompiledArtefact Artefact()
    {
        var tables = BuiltInArtefacts.Tables().Select(t => CompiledArtefact.CompileTable(t)).ToList();
        var definition = BuiltInArtefacts.Definition(
            BuiltInArtefacts.DefaultProductCode, BuiltInArtefacts.DefaultProductVersion, tables.ToDictionary(t => t.Dto.Code, t => t.Hash));
        var byHash = tables.ToDictionary(t => t.Hash, t => t.Dto);
        return CompiledArtefact.Compile(definition, h => byHash[h]);
    }

    private static MotorRisk Risk(string birthDate = "1985-06-15", int claims = 0, string firstRegistration = "2020", string? value = "15000.00", params string[] coverages) =>
        MotorRisk.Parse(JsonSerializer.SerializeToElement(RatingTestSupport.RiskTree(firstRegistration, value, 1400, "PRIVATE", birthDate, claims, coverages)), "seg-1");

    // REQ-RAT-077, -078, -086/-087 (minimum), -100, -102 (explicit rounding): the premium of each coverage is table lookups
    // combined by decimal arithmetic in the rule engine and rounded once, half up, to two places.
    [Theory]
    [InlineData("MTPL", "121.50")] // 135.00 base (1400 cc) x 1.0000 (age 41) x 0.9000 (no claims); minimum 80.00 does not apply
    [InlineData("OWN-DAMAGE", "283.50")] // 15000.00 x 0.0210 = 315.00 x 1.0000 x 1.0000 (6-year-old car) x 0.9000
    [InlineData("WINDSCREEN", "25.00")] // flat; no factor and no minimum row apply
    public void REQ_RAT_077_the_premium_of_a_coverage_is_the_steps_of_the_artefact_applied_in_order(string coverage, string expected)
    {
        var premium = RatingEngine.RateCoverage(Artefact(), Risk(), coverage, Basis);

        premium.Premium.ShouldBe(D(expected));
        ((int)premium.Premium.Scale).ShouldBe(2);
    }

    [Fact]
    public void REQ_RAT_086_the_minimum_premium_lifts_a_lower_result_and_the_trace_says_so()
    {
        // Own damage on a 1,000 car: 1000.00 x 0.0210 x 0.9000 = 18.90, below the 90.00 minimum.
        var premium = RatingEngine.RateCoverage(Artefact(), Risk(value: "1000.00"), "OWN-DAMAGE", Basis);

        premium.Premium.ShouldBe(90.00m);
        var minimum = premium.Steps.Single(s => s.StepId == "MINIMUM_PREMIUM");
        minimum.Applied.ShouldBeTrue();
        D(minimum.Before).ShouldBe(18.90m);
        D(minimum.After).ShouldBe(90.00m);
    }

    [Fact]
    public void REQ_RAT_104_every_step_has_a_greek_and_english_explanation_and_names_the_table_and_row_it_used()
    {
        var premium = RatingEngine.RateCoverage(Artefact(), Risk(), "OWN-DAMAGE", Basis);

        premium.Steps.Select(s => s.StepId).ShouldBe(["BASE_RATE", "DRIVER_AGE", "VEHICLE_AGE", "CLAIMS", "MINIMUM_PREMIUM", "ROUND_PREMIUM"]);
        premium.Steps.ShouldAllBe(s => s.ExplanationEn.Length > 0 && s.ExplanationEl.Length > 0);
        var driver = premium.Steps.Single(s => s.StepId == "DRIVER_AGE");
        driver.RuleId.ShouldBe("AGE-30-64");
        driver.TableHash!.Length.ShouldBe(64);
        driver.Value.ShouldBe("1.0000");
        premium.Steps.Single(s => s.StepId == "VEHICLE_AGE").RuleId.ShouldBe("VAGE-3-7");
        premium.Steps.Single(s => s.StepId == "ROUND_PREMIUM").Applied.ShouldBeTrue();
    }

    [Fact]
    public void A_factor_table_with_no_matching_row_is_skipped_and_shown_as_not_applied()
    {
        var premium = RatingEngine.RateCoverage(Artefact(), Risk(), "WINDSCREEN", Basis);

        premium.Steps.Where(s => s.Kind == "FACTOR").ShouldAllBe(s => !s.Applied);
    }

    // Boundary: the age factor uses whole years on the effective date (REQ-RAT-082 band edges).
    [Theory]
    [InlineData("2001-11-01", "AGE-25-29")] // turns 25 exactly on the effective date
    [InlineData("2001-11-02", "AGE-0-24")] // turns 25 the next day
    [InlineData("1996-11-01", "AGE-30-64")] // turns 30 exactly
    [InlineData("1961-11-02", "AGE-30-64")] // turns 65 the next day
    [InlineData("1961-11-01", "AGE-65-UP")] // turns 65 exactly
    public void The_driver_age_band_follows_the_birthday_on_the_effective_date(string birthDate, string expectedRule)
    {
        var premium = RatingEngine.RateCoverage(Artefact(), Risk(birthDate), "MTPL", Basis);

        premium.Steps.Single(s => s.StepId == "DRIVER_AGE").RuleId.ShouldBe(expectedRule);
    }

    [Fact]
    public void REQ_RAT_046_the_same_artefact_input_and_date_give_the_same_premium_and_trace()
    {
        var artefact = Artefact();
        var first = RatingEngine.RateCoverage(artefact, Risk(claims: 2), "OWN-DAMAGE", Basis);
        var second = RatingEngine.RateCoverage(artefact, Risk(claims: 2), "OWN-DAMAGE", Basis);

        second.Premium.ShouldBe(first.Premium);
        second.Steps.ShouldBe(first.Steps);
        first.Premium.ShouldBe(393.75m); // 315.00 x 1.2500 (two claims)
    }

    [Fact]
    public void REQ_RAT_061_the_artefact_hash_is_stable_and_covers_the_tables_and_the_product()
    {
        var a = Artefact();
        var b = Artefact();
        b.Hash.ShouldBe(a.Hash);
        a.Tables.Values.Select(t => t.Hash).ShouldAllBe(h => h.Length == 64);

        var tables = BuiltInArtefacts.Tables().Select(t => CompiledArtefact.CompileTable(t)).ToDictionary(t => t.Dto.Code, t => t.Hash);
        var other = BuiltInArtefacts.Definition("OTHER_PRODUCT", "1.0", tables);
        ArtefactJson.HashOf(other).ShouldNotBe(a.Hash);
    }

    // D-SLC-04: the tables are illustrative test data and say so everywhere they travel.
    [Fact]
    public void D_SLC_04_the_tables_and_the_artefact_are_marked_as_illustrative_test_data_not_a_tariff()
    {
        var artefact = Artefact();

        artefact.Definition.Metadata.DataStatus.ShouldBe("ILLUSTRATIVE_TEST_DATA");
        artefact.Definition.Metadata.NotATariff.ShouldBeTrue();
        artefact.Definition.Metadata.Note.ShouldContain("not an approved tariff");
        artefact.Tables.Values.ShouldAllBe(t => t.Dto.DataStatus == "ILLUSTRATIVE_TEST_DATA" && t.Dto.Description.Contains("ILLUSTRATIVE TEST DATA", StringComparison.Ordinal));
    }

    [Fact]
    public void REQ_RAT_033_an_attribute_the_input_schema_does_not_declare_is_rejected()
    {
        var tree = RatingTestSupport.RiskTree();
        tree["vehicle"]!["colour"] = "red";

        var ex = Should.Throw<DomainException>(() => MotorRisk.Parse(JsonSerializer.SerializeToElement(tree), "seg-1"));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-INPUT-UNDECLARED");
        ex.Error.Detail!.ShouldContain("vehicle.colour");
    }

    [Fact]
    public void REQ_RAT_031_a_missing_or_malformed_value_is_an_input_error_naming_the_field()
    {
        var tree = RatingTestSupport.RiskTree(value: "-5");

        var ex = Should.Throw<DomainException>(() => MotorRisk.Parse(JsonSerializer.SerializeToElement(tree), "seg-1"));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
        ex.Error.Detail!.ShouldContain("vehicle.vehicleValue");
    }

    [Fact]
    public void A_vehicle_value_is_only_needed_by_a_coverage_rated_on_it_and_unused_product_fields_never_reach_the_hash()
    {
        var withoutValue = RatingTestSupport.RiskTree(value: null);
        var risk = MotorRisk.Parse(JsonSerializer.SerializeToElement(withoutValue), "seg-1");

        RatingEngine.RateCoverage(Artefact(), risk, "MTPL", Basis).Premium.ShouldBe(121.50m);
        var ex = Should.Throw<DomainException>(() => RatingEngine.RateCoverage(Artefact(), risk, "OWN-DAMAGE", Basis));
        ex.Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
        ex.Error.Detail!.ShouldContain("vehicleValue");

        var other = RatingTestSupport.RiskTree();
        other["vehicle"]!["registrationNumber"] = "ZZZ9999"; // P2 and not used in pricing
        MotorRisk.Parse(JsonSerializer.SerializeToElement(other), "seg-1").ToNormalised().ToJsonString()
            .ShouldBe(MotorRisk.Parse(JsonSerializer.SerializeToElement(RatingTestSupport.RiskTree()), "seg-1").ToNormalised().ToJsonString());
        MotorRisk.Parse(JsonSerializer.SerializeToElement(other), "seg-1").ToNormalised().ToJsonString().ShouldNotContain("ZZZ9999");
    }

    [Fact]
    public void REQ_RAT_032_the_normalised_input_does_not_depend_on_the_order_of_coverages()
    {
        var a = Risk(coverages: ["WINDSCREEN", "MTPL"]).ToNormalised().ToJsonString();
        var b = Risk(coverages: ["MTPL", "WINDSCREEN"]).ToNormalised().ToJsonString();

        a.ShouldBe(b);
    }

    // REQ-RAT-109/-110: tax = base x configured rate, rounded as the plan declares (half up, 2 places).
    [Theory]
    [InlineData("121.50", "0.15", "18.23")] // 18.225 rounds up
    [InlineData("283.50", "0.15", "42.53")] // 42.525
    [InlineData("67.50", "0.15", "10.13")] // 10.125
    [InlineData("25.00", "0.15", "3.75")]
    [InlineData("121.50", "0.05", "6.08")] // 6.075
    public void REQ_RAT_109_the_tax_amount_is_rate_times_premium_rounded_half_up(string premium, string rate, string expected)
    {
        var plan = BuiltInArtefacts.Definition("P", "1.0", new Dictionary<string, string>()).TaxPlan[0];

        var amount = RatingEngine.TaxAmount(new Money(D(premium), Currency.EUR), D(rate), plan);

        amount.Amount.ShouldBe(D(expected));
        amount.Currency.ShouldBe(Currency.EUR);
    }

    // D-ARC-27: precision loss raises an error instead of rounding silently.
    [Fact]
    public void D_ARC_27_a_tax_calculation_that_cannot_be_represented_exactly_raises_RAT_ERR_SCALE()
    {
        var plan = BuiltInArtefacts.Definition("P", "1.0", new Dictionary<string, string>()).TaxPlan[0];
        var premium = new Money(0.1234567890123456789012345678m, Currency.EUR);

        var ex = Should.Throw<DomainException>(() => RatingEngine.TaxAmount(premium, 0.1234567890123456789012345678m, plan));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-SCALE");
    }

    [Theory]
    [InlineData("\"0.15\"", "GENERAL", "0.15")]
    [InlineData("0.2", "GENERAL", "0.2")]
    [InlineData("{\"GENERAL\":\"0.15\",\"FIRE\":\"0.20\"}", "FIRE", "0.20")]
    public void REQ_RAT_109_a_configured_rate_is_a_number_a_string_or_an_object_keyed_by_tax_class(string json, string taxClass, string expected)
    {
        var rate = RatingEngine.ReadRate(JsonDocument.Parse(json).RootElement, taxClass, "tax.test.rate");

        rate.ShouldBe(D(expected));
    }

    [Theory]
    [InlineData("{\"GENERAL\":\"0.15\"}", "FIRE")] // no rate for the class
    [InlineData("\"1.5\"", "GENERAL")] // more than 100%
    [InlineData("\"-0.1\"", "GENERAL")]
    [InlineData("\"abc\"", "GENERAL")]
    [InlineData("true", "GENERAL")]
    public void REQ_RAT_112_a_missing_or_nonsensical_configured_rate_fails_closed_with_RAT_ERR_TAX(string json, string taxClass)
    {
        var ex = Should.Throw<DomainException>(() => RatingEngine.ReadRate(JsonDocument.Parse(json).RootElement, taxClass, "tax.test.rate"));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-TAX");
    }

    [Fact]
    public void A_table_whose_hash_differs_from_the_one_the_artefact_pins_is_refused()
    {
        var tables = BuiltInArtefacts.Tables().Select(t => CompiledArtefact.CompileTable(t)).ToList();
        var definition = BuiltInArtefacts.Definition("P", "1.0", tables.ToDictionary(t => t.Dto.Code, t => t.Hash));
        var original = BuiltInArtefacts.Tables().Single(t => t.Code == "BASE_RATE");
        var rules = original.Rules.ToList();
        rules[0] = rules[0] with { Outputs = ["999.00", "\"FLAT\""] };
        var tampered = original with { Rules = rules };

        var ex = Should.Throw<DomainException>(() => CompiledArtefact.Compile(definition, _ => tampered));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-UNKNOWN-ARTEFACT");
    }
}
