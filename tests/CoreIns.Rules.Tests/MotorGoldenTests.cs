using System;
using System.Linq;
using CoreIns.Rules.DecisionTables;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

/// <summary>
/// Golden tests: hand-computed expected premiums and decisions for realistic motor rules. Any cent difference fails.
/// Policy effective date 2026-11-01.
/// </summary>
public class MotorGoldenTests
{
    [Theory]
    // case, main driver birth, vehicle value, NCD years, claims, power kW, base rate, expected premium
    [InlineData("A: 36y, 12k, NCD 6", "1990-06-15", "12000", 6, 0, 85, "400.00", "240.00")]
    [InlineData("B: 21y, 22k, NCD 1, 110 kW", "2005-03-10", "22000", 1, 0, 110, "400.00", "819.72")]
    [InlineData("C: 70y, 4k, 2 claims", "1955-12-01", "4000", 4, 2, 60, "400.00", "414.00")]
    [InlineData("D: 41y today, 75k, NCD 3 with 1 claim step-back", "1985-11-01", "75000", 3, 1, 150, "400.00", "598.40")]
    [InlineData("E: minimum premium applies", "1980-01-01", "3000", 10, 0, 50, "250.00", "150.00")]
    [InlineData("F: band lower bounds inclusive, HalfUp rounding point", "1999-05-20", "15000", 2, 0, 100, "333.33", "448.50")]
    [InlineData("G: 75y exactly, band 60k inclusive", "1951-11-01", "60000", 0, 0, 101, "400.00", "985.60")]
    public void Premium_golden_cases(string name, string birth, string value, int ncd, int claims, int powerKw, string baseRate, string expected)
    {
        var inputs = MotorFixtures.Inputs(Date(birth), decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), ncd, claims, powerKw);
        MotorFixtures.Rate(inputs, decimal.Parse(baseRate, System.Globalization.CultureInfo.InvariantCulture), powerKw).ShouldBe(expected, name);
    }

    [Fact]
    public void Under_age_driver_has_no_rating_band()
    {
        var inputs = MotorFixtures.Inputs(Date("2008-11-02"), 10000m, 0, 0);
        var result = MotorFixtures.AgeTable.Evaluate(inputs, MotorFixtures.AsOf);
        result.IsSuccess.ShouldBeTrue();
        result.Match.ShouldBeNull();
        result.Trace.Variables.Single().ToString().ShouldBe("mainDriverAge = 17");
    }

    [Theory]
    [InlineData("2008-11-02", 85, "PRIVATE", "UW-AGE-U18", "DECLINE", "KNOCKOUT", "UW.AGE.UNDER18")]
    [InlineData("2006-03-10", 110, "PRIVATE", "UW-YOUNG-POWER", "REFER", "SENIOR", "UW.YOUNG.HIGHPOWER")]
    [InlineData("2006-03-10", 100, "PRIVATE", "UW-ACCEPT", "ACCEPT", "AUTO", "UW.NONE")]
    [InlineData("2005-03-10", 110, "PRIVATE", "UW-ACCEPT", "ACCEPT", "AUTO", "UW.NONE")]
    [InlineData("1990-06-15", 85, "TAXI", "UW-USAGE", "REFER", "STANDARD", "UW.USAGE.TAXI")]
    [InlineData("1946-10-01", 85, "PRIVATE", "UW-AGE-80", "REFER", "STANDARD", "UW.AGE.OVER80")]
    [InlineData("1946-10-01", 85, "RIDESHARE", "UW-USAGE", "REFER", "STANDARD", "UW.USAGE.RIDESHARE")]
    [InlineData("1990-06-15", 85, "PRIVATE", "UW-ACCEPT", "ACCEPT", "AUTO", "UW.NONE")]
    public void Underwriting_golden_cases(string birth, int powerKw, string usage, string rule, string decision, string lane, string issueKey)
    {
        var result = MotorFixtures.UwTable.Evaluate(MotorFixtures.Inputs(Date(birth), 10000m, 0, 0, powerKw, usage), MotorFixtures.AsOf);
        result.MatchedRuleIds.ShouldBe(new[] { rule });
        result.Match!.Outputs.Select(o => o.ToString()).ShouldBe(new[]
        {
            $"decision = \"{decision}\"", $"lane = \"{lane}\"", $"issueKey = \"{issueKey}\"",
        });
    }

    [Fact]
    public void Surcharges_are_collected_in_row_order()
    {
        var young = MotorFixtures.MakeDriver(Date("2004-01-01"), isMain: false);
        var inputs = MotorFixtures.Inputs(Date("1980-01-01"), 20000m, 5, 0, powerKw: 130, territory: "ATTICA", tracker: true, otherDrivers: new[] { young });
        var result = MotorFixtures.SurchargeTable.Evaluate(inputs, MotorFixtures.AsOf);
        result.MatchedRuleIds.ShouldBe(new[] { "SUR-YOUNG", "SUR-POWER", "SUR-URBAN", "DISC-TRACKER" });
        result.Matches.Sum(m => ((DecimalValue)m.Output("loading")).Value).ShouldBe(0.30m);

        var noTracker = MotorFixtures.Inputs(Date("1980-01-01"), 20000m, 5, 0, territory: "CRETE");
        MotorFixtures.SurchargeTable.Evaluate(noTracker, MotorFixtures.AsOf).MatchedRuleIds.ShouldBe(new[] { "SUR-NOTRACKER" });

        var trackerOff = MotorFixtures.Inputs(Date("1980-01-01"), 20000m, 5, 0, territory: "CRETE", tracker: false);
        MotorFixtures.SurchargeTable.Evaluate(trackerOff, MotorFixtures.AsOf).MatchedRuleIds.ShouldBeEmpty();
    }

    [Fact]
    public void Ncd_priority_prefers_claims_rules()
    {
        MotorFixtures.NcdTable.Evaluate(MotorFixtures.Inputs(Date("1980-01-01"), 1m, 7, 2), MotorFixtures.AsOf).Match!.RuleId.ShouldBe("NCD-CLAIMS2");
        MotorFixtures.NcdTable.Evaluate(MotorFixtures.Inputs(Date("1980-01-01"), 1m, 7, 1), MotorFixtures.AsOf).Match!.RuleId.ShouldBe("NCD-CLAIMS1");
        MotorFixtures.NcdTable.Evaluate(MotorFixtures.Inputs(Date("1980-01-01"), 1m, 2, 1), MotorFixtures.AsOf).Match!.RuleId.ShouldBe("NCD-1");
        MotorFixtures.NcdTable.Evaluate(MotorFixtures.Inputs(Date("1980-01-01"), 1m, 0, 0), MotorFixtures.AsOf).Match!.RuleId.ShouldBe("NCD-0");
    }

    [Fact]
    public void Golden_table_hashes_are_stable()
    {
        // Re-baselining these needs approval (rating golden rule): a change means the table content changed.
        var again = CompiledDecisionTable.Compile(MotorFixtures.AgeDefinition, MotorFixtures.Env);
        again.ContentHash.ShouldBe(MotorFixtures.AgeTable.ContentHash);
        MotorFixtures.AgeTable.CanonicalText.ShouldStartWith("18:decision-table/1.0;10:hit=UNIQUE;11:variables=1;13:mainDriverAge;3:int;");
        MotorFixtures.AgeTable.ContentHash.ShouldBe(PinnedAgeTableHash);
    }

    /// <summary>Pinned content hash of the age-band table (changes only when its content or the language changes).</summary>
    private const string PinnedAgeTableHash = "3f9b445c012179d8c0ab2eeb2e278909411fd194b00e2e7da6bc0224f11d6a22";

    private static DateOnly Date(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
