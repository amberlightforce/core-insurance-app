using System.Text.Json;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Errors;
using Microsoft.Extensions.Configuration;

namespace CoreIns.IntegrationTests.Market;

/// <summary>BO-MKT-06: statutory clock sets and regime codes. Pure code data, no database.</summary>
public sealed class StatutoryClockAndCodeTests
{
    private static StatutoryClockAndCodeService Service(string country = "GR") =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stamp:Country"] = country }).Build());

    [Fact]
    public void An_unverified_clock_returns_the_fail_closed_flag()
    {
        var item = Service().GetClock("CLM_MTPL_OFFER").Values!.Value;
        item.GetProperty("legalStatus").GetString().ShouldBe("Unverified");
        item.GetProperty("failClosed").GetBoolean().ShouldBeTrue();
        item.GetProperty("usableInProduction").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void A_settled_clock_is_usable_and_the_set_is_flagged_while_any_value_is_not_settled()
    {
        var dsar = Service().GetClock("CMP_DSAR_RESPONSE").Values!.Value;
        dsar.GetProperty("failClosed").GetBoolean().ShouldBeFalse();
        var set = Service().ListClocks(null, null).Values!.Value;
        set.GetProperty("items").GetArrayLength().ShouldBe(9);
        set.GetProperty("failClosed").GetBoolean().ShouldBeTrue();
        Service().ListClocks("CMP_DSAR_RESPONSE", "GR").Values!.Value.GetProperty("failClosed").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void A_jurisdiction_without_values_is_empty_and_fail_closed_and_an_unknown_clock_is_refused()
    {
        var set = Service("CY").ListClocks(null, null).Values!.Value;
        set.GetProperty("items").GetArrayLength().ShouldBe(0);
        set.GetProperty("failClosed").GetBoolean().ShouldBeTrue();
        Should.Throw<DomainException>(() => Service("CY").GetClock("CLM_MTPL_OFFER"));
        Should.Throw<DomainException>(() => Service().GetClock("NOPE"));
    }

    [Fact]
    public void Regime_code_lists_validate_and_carry_the_unverified_flag()
    {
        StatutoryClockAndCodeService.ListCodes(null).Items.Count.ShouldBe(2);
        var ok = StatutoryClockAndCodeService.Validate(new RegimeCodeValidateRequest { Regime = JsonSerializer.SerializeToElement("SII_LOB"), Code = JsonSerializer.SerializeToElement("4") });
        ok.Validity!.Value.GetProperty("valid").GetBoolean().ShouldBeTrue();
        ok.Validity.Value.GetProperty("failClosed").GetBoolean().ShouldBeTrue();
        var bad = StatutoryClockAndCodeService.Validate(new RegimeCodeValidateRequest { Regime = JsonSerializer.SerializeToElement("SII_LOB"), Code = JsonSerializer.SerializeToElement("99") });
        bad.Validity!.Value.GetProperty("valid").GetBoolean().ShouldBeFalse();
        Should.Throw<DomainException>(() => StatutoryClockAndCodeService.GetCodes("NOPE"));
        StatutoryClockAndCodeService.GetCodes("sii_lob").Codes!.Value.GetProperty("codes").GetArrayLength().ShouldBe(12);
    }

    [Fact]
    public void The_crosswalk_round_trips_for_every_internal_code()
    {
        foreach (var code in new[] { "MTPL", "MOTOR_OD", "FIRE_PROPERTY", "GENERAL_LIABILITY", "MARINE_TRANSPORT", "LEGAL_EXPENSES", "ASSISTANCE", "MEDICAL" })
        {
            var there = StatutoryClockAndCodeService.Crosswalk("INTERNAL_LOB", "SII_LOB", code).Codes!.Value.GetProperty("mapped").EnumerateArray().Select(e => e.GetString()!).ToList();
            there.Count.ShouldBe(1);
            var back = StatutoryClockAndCodeService.Crosswalk("SII_LOB", "INTERNAL_LOB", there[0]).Codes!.Value.GetProperty("mapped").EnumerateArray().Select(e => e.GetString()).ToList();
            back.ShouldContain(code);
        }

        StatutoryClockAndCodeService.Crosswalk("INTERNAL_LOB", "SII_LOB", "MTPL").Codes!.Value.GetProperty("mapped")[0].GetString().ShouldBe("4");
        StatutoryClockAndCodeService.Crosswalk("INTERNAL_LOB", "SII_LOB", "NOPE").Validity!.Value.GetProperty("mappedAny").GetBoolean().ShouldBeFalse();
        Should.Throw<DomainException>(() => StatutoryClockAndCodeService.Crosswalk("INTERNAL_LOB", null, "MTPL"));
    }
}
