using CoreIns.CountryPacks.CY;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;

namespace CoreIns.CountryPacks.Tests;

/// <summary>
/// The shipped versions of the pack data (SL5-MKT-STATE, D-SL5-07, REQ-MKT-129): GR 0.1.0 is the content before the treatment rows, 0.2.0 adds them
/// (MINOR) and 0.3.0 adds the levy rows of a distance-withdrawal void (NX-HD-06); the newest version is what the base interface serves, so a consumer that knows no versions is unchanged.
/// </summary>
public sealed class PackVersionsTests
{
    private static bool IsTreatment(PackConfigValue value) => value.Key.StartsWith("tax.treatment.", StringComparison.Ordinal);

    [Fact]
    public void GR_0_2_0_is_0_1_0_plus_exactly_the_treatment_rows_and_0_3_0_adds_only_the_levy_rows_and_is_the_newest_version()
    {
        var pack = new GrPackConfiguration();

        pack.Versions.Select(v => v.Version).ShouldBe([GrPackConfiguration.FirstVersion, GrPackConfiguration.SecondVersion, GrPackConfiguration.Version]);
        pack.PackVersion.ShouldBe("0.3.0");
        pack.Values.ShouldBe([.. pack.Versions[2].Values]);
        pack.Versions[0].Values.Any(IsTreatment).ShouldBeFalse();
        pack.Versions[1].Values.Where(v => !IsTreatment(v)).ToList().ShouldBe([.. pack.Versions[0].Values]);
        pack.Versions[1].Values.Count(IsTreatment).ShouldBe(10);
        pack.Versions[1].Values.ShouldAllBe(v => !v.Key.StartsWith("tax.treatment.rule.LEVY.", StringComparison.Ordinal));

        // 0.3.0 = 0.2.0 + the two levy rows of a distance-withdrawal void (NX-HD-06), nothing else.
        pack.Versions[2].Values.Take(pack.Versions[1].Values.Count).ToList().ShouldBe([.. pack.Versions[1].Values]);
        pack.Versions[2].Values.Skip(pack.Versions[1].Values.Count).Select(v => v.Key).ShouldBe(
            ["tax.treatment.rule.LEVY.VOID.DistanceWithdrawal", "tax.treatment.rule.LEVY.DISTANCE_WITHDRAWAL_VOID.ANY"]);
        pack.Versions[2].Values.ShouldAllBe(v => !v.Key.StartsWith("tax.treatment.rule.LEVY.", StringComparison.Ordinal) || v.LegalStatus == LegalStatus.PendingOpinion);
    }

    /// <summary>
    /// A shipped version never changes (REQ-MKT-129, D-SL5-07): a database that registered it holds this digest and refuses a release that
    /// ships other content under the same version (PackVersionChangedException). NX-HD-06 once edited 0.2.0 in place and made main unable
    /// to start on an existing database; these digests are what such databases hold. Change the content = ship a new version, never a new digest here.
    /// </summary>
    [Theory]
    [InlineData("0.2.0", "3f7c2f62f4a8c06a068eedc7d056927dac8817f0a6fcd363bf47668b270477a8")]
    [InlineData("0.1.0", "808b5fe0934ab15ec48784a113c4527019260dfe75c06ff6b425815df1869453")]
    public void Shipped_GR_versions_keep_the_digest_recorded_by_existing_databases(string version, string digest)
    {
        var content = new GrPackConfiguration().Versions.Single(v => v.Version == version).Values;

        PackVersionContent.DigestOf(content).ToString().ShouldBe(digest);
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
