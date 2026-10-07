using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.IntegrationTests.Market;

/// <summary>
/// SL-MKT unit tests of the resolver, the Production gate and rounding over the Greece pack data (no database).
/// Requirement ids in test names: REQ-MKT-044/045/046 resolve, REQ-MKT-343 gate, REQ-MKT-192/193/195 rounding.
/// </summary>
public sealed class ConfigurationEngineTests
{
    private static readonly BusinessDate TaxPoint = new(2027, 1, 15);

    private static readonly LegalEntityInfo Entity = new(
        new LegalEntityId(Guid.Parse("0192f0c4-0000-7000-8000-000000000001")), LegalEntityCode.Parse("GR-TEST"), "GR", "gr", "EUR", "Europe/Athens", "ACTIVE", true);

    private static ConfigurationEngine Engine(string environment, params IPackConfigurationSource[] sources)
    {
        var clock = new ManualClock(Instant.FromUtc(2026, 10, 7));
        var catalogue = new ConfigurationCatalogue(sources.Length == 0 ? [new GrPackConfiguration()] : sources, clock.Now);
        return new ConfigurationEngine(catalogue, new LegalEntityRegistry([Entity]), new FakeEnvironment(environment), clock);
    }

    private static ConfigurationResolveRequest Request(params string[] keys) => new()
    {
        LegalEntity = "GR-TEST",
        Jurisdiction = "GR",
        Keys = keys,
        TimeBasisDates = new Dictionary<string, BusinessDate> { [TimeBases.TaxPointDate] = TaxPoint },
    };

    [Fact]
    public void REQ_MKT_044_ipt_rates_resolve_with_status_source_and_a_visible_provisional_flag_in_development()
    {
        var response = Engine("Development").Resolve(Request("tax.ipt.rate.general", "tax.ipt.liability_point"), ValidAt.From(TaxPoint), null);

        var rate = response.Values.Single(v => v.Key == "tax.ipt.rate.general");
        rate.Value.GetString().ShouldBe("0.15");
        rate.LegalStatus.ShouldBe(ConfigurationResolveResponse.ValueItem.LegalStatusValue.Settled);
        rate.LegalSourceRef!.ShouldContain("Law 5177/2025");
        rate.SourceLayer.ShouldBe("country:GR");
        rate.Provisional.ShouldBeFalse();
        var point = response.Values.Single(v => v.Key == "tax.ipt.liability_point");
        point.Value.GetString().ShouldBe("DUE");
        point.LegalStatus.ShouldBe(ConfigurationResolveResponse.ValueItem.LegalStatusValue.PendingOpinion);
        point.Provisional.ShouldBeTrue();
        response.HasProvisionalValues.ShouldBeTrue();
    }

    [Fact]
    public void REQ_MKT_343_production_refuses_every_value_that_is_not_settled_and_names_it()
    {
        var engine = Engine("Production");

        var refused = Should.Throw<DomainException>(() => engine.Resolve(Request("tax.ipt.rate.general", "tax.levy.auxfund.ceiling_rate", "tax.levy.auxfund.split.insurer_share"), null, null));

        refused.Error.Code.ToString().ShouldBe("MKT-ERR-CFG-NOT-SETTLED");
        refused.Error.Detail!.ShouldContain("tax.levy.auxfund.ceiling_rate (PendingOpinion)");
        refused.Error.Detail!.ShouldContain("tax.levy.auxfund.split.insurer_share (Unverified)");
        refused.Error.Detail!.ShouldNotContain("tax.ipt.rate.general");
        engine.Resolve(Request("tax.ipt.rate.general", "tax.ipt.rate.fire"), null, null).HasProvisionalValues.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void REQ_MKT_343_non_production_environments_serve_unsettled_values_flagged(string environment)
    {
        var response = Engine(environment).Resolve(Request("tax.levy.auxfund.ceiling_rate"), null, null);

        response.Values.Single().Provisional.ShouldBeTrue();
        response.Values.Single().Value.GetString().ShouldBe("0.06");
    }

    [Fact]
    public void D_REG_01_values_the_prds_do_not_state_are_empty_keys_that_fail_closed()
    {
        var response = Engine("Development").Resolve(
            Request("tax.levy.auxfund.ph_stamp_duty_rate", "tax.levy.auxfund.components", "tax.levy.auxfund.cancellation_treatment", "tax.levy.auxfund.midterm_base"), null, null);

        response.Values.ShouldBeEmpty();
        response.MissingKeys.Count.ShouldBe(4);
    }

    [Fact]
    public void D_REG_06_neither_auxiliary_fund_percentage_pair_is_encoded()
    {
        var all = new GrPackConfiguration().Values;

        all.Any(v => v.Value is "0.042" or "0.018" or "0.045" or "0.015").ShouldBeFalse();
        all.Single(v => v.Key == "tax.levy.auxfund.split.insurer_share").LegalStatus.ShouldBe(LegalStatus.Unverified);
    }

    [Fact]
    public void Every_pack_value_has_a_registered_key_a_matching_type_and_a_source()
    {
        // The catalogue constructor validates all of it; building it is the test.
        var catalogue = new ConfigurationCatalogue([new GrPackConfiguration()], Instant.FromUtc(2026, 10, 7));

        catalogue.Entries.ShouldAllBe(e => e.SourceRef.Length > 0);
        catalogue.Entries.Where(e => e.LegalStatus != LegalStatus.NotRegulatory && !e.IsCore).ShouldAllBe(e => e.SourceRef.Contains("PRD-17", StringComparison.Ordinal));
    }

    [Fact]
    public void REQ_MKT_043_a_tax_key_without_a_tax_point_date_is_an_error_not_a_guess()
    {
        var request = new ConfigurationResolveRequest { LegalEntity = "GR-TEST", Jurisdiction = "GR", Keys = ["tax.ipt.rate.general"] };

        Should.Throw<DomainException>(() => Engine("Development").Resolve(request, ValidAt.From(TaxPoint), null))
            .Error.Code.ToString().ShouldBe("MKT-ERR-CFG-TIMEBASIS-MISSING");
    }

    [Fact]
    public void REQ_MKT_033_an_unregistered_key_and_an_unknown_legal_entity_are_refused()
    {
        var engine = Engine("Development");

        Should.Throw<DomainException>(() => engine.Resolve(Request("tax.made.up"), null, null)).Error.Code.ToString().ShouldBe("MKT-ERR-CFG-UNKNOWN-KEY");
        Should.Throw<DomainException>(() => engine.Resolve(Request("tax.ipt.rate.general") with { LegalEntity = "XX-NONE" }, null, null))
            .Error.Code.ToString().ShouldBe("MKT-ERR-CFG-LEGAL-ENTITY-UNKNOWN");
    }

    [Fact]
    public void REQ_MKT_044_a_namespace_returns_every_key_with_a_value_and_lists_the_empty_ones()
    {
        var response = Engine("Development").Resolve(
            new ConfigurationResolveRequest { LegalEntity = "GR-TEST", Jurisdiction = "GR", Namespace = "tax.levy.auxfund" }, null, null);

        response.Values.Select(v => v.Key).ShouldContain("tax.levy.auxfund.ceiling_rate");
        response.MissingKeys.ShouldContain("tax.levy.auxfund.ph_stamp_duty_rate");
    }

    [Fact]
    public void REQ_MKT_045_046_resolution_is_deterministic_and_the_hash_follows_the_data()
    {
        var first = Engine("Development");
        var second = Engine("Development");
        var request = Request("tax.ipt.rate.general");

        System.Text.Json.JsonSerializer.Serialize(first.Resolve(request, null, null))
            .ShouldBe(System.Text.Json.JsonSerializer.Serialize(second.Resolve(request, null, null)), "same state, same context, same answer");
        first.Catalogue.Hash.ShouldBe(second.Catalogue.Hash);
        first.Catalogue.Hash.ToString().Length.ShouldBe(64);

        var changed = new ConfigurationCatalogue([new ChangedRate()], Instant.FromUtc(2026, 10, 7));
        changed.Hash.ShouldNotBe(first.Catalogue.Hash);
        Should.Throw<DomainException>(() => first.Resolve(request with { ConfigurationHash = changed.Hash }, null, null))
            .Error.Code.ToString().ShouldBe("MKT-ERR-CFG-HASH-UNKNOWN");
    }

    [Fact]
    public void REQ_MKT_190_the_greek_entity_has_euro_as_transaction_and_functional_currency()
    {
        var response = Engine("Production").Resolve(Request("cur.transaction", "cur.functional"), null, null);

        response.Values.ShouldAllBe(v => v.Value.GetString() == "EUR" && !v.Provisional);
    }

    [Theory]
    [InlineData("2.345", "2.35", "-0.005")]
    [InlineData("2.344", "2.34", "0.004")]
    [InlineData("-2.345", "-2.35", "0.005")]
    public void REQ_MKT_192_195_charge_lines_round_half_up_to_the_euro_minor_unit(string amount, string rounded, string residual)
    {
        var response = Engine("Production").ApplyRounding(Rounding(amount, "charge.line"));

        response.AmountAfterRounding!.Value.Amount.ShouldBe(decimal.Parse(rounded, System.Globalization.CultureInfo.InvariantCulture));
        response.Residual!.Value.Amount.ShouldBe(decimal.Parse(residual, System.Globalization.CultureInfo.InvariantCulture));
        response.RuleKey.ShouldBe("cur.rounding.charge.line");
        response.Scale.ShouldBe(2);
        response.RuleId.ShouldNotBeNull();
    }

    [Fact]
    public void REQ_MKT_195_the_same_inputs_give_the_same_rule_id_for_every_caller()
    {
        var a = Engine("Development").ApplyRounding(Rounding("10.005", "charge.line"));
        var b = Engine("Development").ApplyRounding(Rounding("10.005", "charge.line"));

        System.Text.Json.JsonSerializer.Serialize(a).ShouldBe(System.Text.Json.JsonSerializer.Serialize(b));
    }

    [Fact]
    public void REQ_MKT_193_tax_line_rounding_is_the_unverified_core_default_and_production_refuses_it()
    {
        var development = Engine("Development").ApplyRounding(Rounding("1.239", "tax.line", taxClass: "general"));

        development.AmountAfterRounding!.Value.Amount.ShouldBe(1.24m);
        development.RuleKey.ShouldBe("cur.rounding.tax.line", "no tax-class rule exists, so the purpose rule applies (BR-MKT-027)");
        development.Provisional.ShouldBe(true);
        Should.Throw<DomainException>(() => Engine("Production").ApplyRounding(Rounding("1.239", "tax.line")))
            .Error.Code.ToString().ShouldBe("MKT-ERR-CFG-NOT-SETTLED");
    }

    [Fact]
    public void REQ_MKT_192_an_unknown_purpose_falls_back_to_the_currency_default()
    {
        Engine("Production").ApplyRounding(Rounding("5.555", "made.up")).RuleKey.ShouldBe("cur.rounding.default");
    }

    [Theory]
    [InlineData("HALF_UP", "1.235", "1.24")]
    [InlineData("HALF_EVEN", "1.235", "1.24")]
    [InlineData("HALF_EVEN", "1.225", "1.22")]
    [InlineData("DOWN", "1.239", "1.23")]
    [InlineData("UP", "1.231", "1.24")]
    [InlineData("UP", "-1.231", "-1.24")]
    [InlineData("CEILING", "-1.239", "-1.23")]
    [InlineData("FLOOR", "-1.231", "-1.24")]
    public void REQ_MKT_192_every_rounding_mode_is_exact(string mode, string amount, string expected)
    {
        var rule = RoundingRule.Parse(System.Text.Json.JsonDocument.Parse($$"""{"mode":"{{mode}}","scale":2,"level":"LINE"}""").RootElement);

        rule.Apply(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), 2)
            .ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static RoundingApplyRequest Rounding(string amount, string purpose, string? taxClass = null) => new()
    {
        Amount = Money.Of(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "EUR"),
        Currency = Currency.EUR,
        Purpose = purpose,
        Context = new RoundingApplyRequest.ContextDetail { LegalEntity = "GR-TEST", TaxClass = taxClass },
    };

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ChangedRate : IPackConfigurationSource
    {
        public string PackId => "gr";

        public string PackVersion => "9.9.9";

        public string Country => "GR";

        public IReadOnlyList<PackConfigValue> Values { get; } =
        [
            new("tax.ipt.rate.general", ConfigValueType.ExactDecimal, "0.16", LegalStatus.Draft, "test", MotorPath: true),
        ];
    }
}
