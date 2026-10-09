using CoreIns.CountryPacks.CY;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.Tests;

/// <summary>
/// The shipped versions of the pack data (SL5-MKT-STATE, D-SL5-07, REQ-MKT-129): GR 0.1.0 is the content before the treatment rows, 0.2.0 adds them
/// (MINOR) and nothing else; the newest version is what the base interface serves, so a consumer that knows no versions is unchanged.
/// </summary>
public sealed class PackVersionsTests
{
    private static bool IsTreatment(PackConfigValue value) => value.Key.StartsWith("tax.treatment.", StringComparison.Ordinal);

    [Fact]
    public void GR_0_2_0_is_0_1_0_plus_exactly_the_treatment_rows_and_is_the_newest_version()
    {
        var pack = new GrPackConfiguration();

        pack.Versions.Select(v => v.Version).ShouldBe([GrPackConfiguration.FirstVersion, GrPackConfiguration.Version]);
        pack.PackVersion.ShouldBe("0.2.0");
        pack.Values.ShouldBe([.. pack.Versions[1].Values]);
        pack.Versions[0].Values.Any(IsTreatment).ShouldBeFalse();
        pack.Versions[1].Values.Where(v => !IsTreatment(v)).ToList().ShouldBe([.. pack.Versions[0].Values]);
        pack.Versions[1].Values.Count(IsTreatment).ShouldBe(9);
    }

    [Fact]
    public void GR_0_1_0_keeps_the_values_the_cancellation_rule_does_not_need_and_has_no_servicing_rule()
    {
        var keys = new GrPackConfiguration().Versions[0].Values.Select(v => v.Key).ToList();

        keys.ShouldContain("tax.ipt.rate.general");
        keys.ShouldContain("cur.transaction");
        keys.ShouldNotContain(k => k.Contains("CANCELLATION", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_version_of_every_pack_is_semantic_and_unique()
    {
        foreach (var pack in new IVersionedPackConfigurationSource[] { new GrPackConfiguration(), new CyTreatmentRules() })
        {
            pack.Versions.Select(v => v.Version).ShouldBe([.. pack.Versions.Select(v => v.Version).Distinct()]);
            pack.Versions.ShouldAllBe(v => System.Text.RegularExpressions.Regex.IsMatch(v.Version, "^[0-9]+[.][0-9]+[.][0-9]+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)));
            pack.Versions[^1].Version.ShouldBe(pack.PackVersion);
        }
    }

    [Fact]
    public void CY_0_1_0_carried_no_value_and_0_2_0_is_the_synthetic_stub_with_the_rule_version_unchanged()
    {
        var pack = new CyTreatmentRules();

        pack.Versions[0].Version.ShouldBe("0.1.0");
        pack.Versions[0].Values.ShouldBeEmpty();
        pack.Versions[1].Version.ShouldBe("0.2.0");
        pack.Values.ShouldBe([.. pack.Versions[1].Values]);
        pack.Values.ShouldAllBe(v => v.SourceRef.Contains("SYNTHETIC", StringComparison.Ordinal) && v.Value.Contains("\"ruleVersion\":\"0.1.0\"", StringComparison.Ordinal));
    }
}
