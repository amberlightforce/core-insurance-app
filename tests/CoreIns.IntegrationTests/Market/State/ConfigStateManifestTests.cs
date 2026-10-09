using CoreIns.CountryPacks.CY;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.IntegrationTests.Market.State;

/// <summary>
/// The state manifest and pack-version digests (REQ-MKT-046/047, REQ-MKT-125/129, properties P-06): no database. The hash is the hash of the
/// canonical manifest, so it ignores order and changes with any content.
/// </summary>
public sealed class ConfigStateManifestTests
{
    private static readonly Sha256Hash Core = CoreDefaults.Content.Digest;

    private static ManifestPack Pack(string id, string version, string digestSeed) =>
        new(id, version, Sha256Hash.ComputeUtf8(digestSeed), id == "gr" ? "GR" : "CY");

    private static ConfigStateManifest Genesis(params ManifestPack[] packs) => new(packs, Core, null, StateCauses.Genesis);

    [Fact]
    public void P_06_the_same_manifest_in_any_insertion_order_gives_the_same_hash()
    {
        var a = Pack("gr", "0.2.0", "gr-0.2.0");
        var b = Pack("cy-stub", "0.2.0", "cy-0.2.0");
        var c = Pack("xx", "1.0.0", "xx-1.0.0");

        var hashes = new[] { Genesis(a, b, c), Genesis(c, b, a), Genesis(b, a, c), Genesis(b, c, a) }.Select(m => m.Hash).Distinct().ToList();

        hashes.Count.ShouldBe(1);
    }

    [Fact]
    public void P_06_any_change_to_the_manifest_gives_a_different_hash()
    {
        var gr = Pack("gr", "0.2.0", "gr-0.2.0");
        var baseline = Genesis(gr);
        var parent = baseline.Hash;
        var variants = new Dictionary<string, ConfigurationHash>
        {
            ["version"] = Genesis(gr with { Version = "0.2.1" }).Hash,
            ["digest of the same version"] = Genesis(gr with { Digest = Sha256Hash.ComputeUtf8("tampered") }).Hash,
            ["pack id"] = Genesis(gr with { PackId = "grx" }).Hash,
            ["country"] = Genesis(gr with { Country = "CY" }).Hash,
            ["extra pack"] = Genesis(gr, Pack("cy-stub", "0.1.0", "cy")).Hash,
            ["no pack"] = Genesis().Hash,
            ["core digest"] = (baseline with { CoreDigest = Sha256Hash.ComputeUtf8("other core") }).Hash,
            ["parent and cause"] = new ConfigStateManifest([gr], Core, parent, StateCauses.PackRollback).Hash,
            ["cause alone"] = new ConfigStateManifest([gr], Core, parent, StateCauses.PackActivation).Hash,
        };

        variants.Values.ShouldNotContain(baseline.Hash);
        variants.Values.Distinct().Count().ShouldBe(variants.Count, "every variant is a different state");
    }

    [Fact]
    public void Re_activating_a_combination_seen_before_is_a_new_state_because_the_parent_is_part_of_the_manifest()
    {
        var gr2 = Pack("gr", "0.2.0", "gr-0.2.0");
        var gr1 = Pack("gr", "0.1.0", "gr-0.1.0");
        var genesis = Genesis(gr2);
        var rollback = new ConfigStateManifest([gr1], Core, genesis.Hash, StateCauses.PackRollback);
        var forward = new ConfigStateManifest([gr2], Core, rollback.Hash, StateCauses.PackActivation);

        forward.Hash.ShouldNotBe(genesis.Hash);
        forward.Packs.ShouldBe(genesis.Packs);
    }

    [Fact]
    public void A_manifest_round_trips_through_its_json_and_keeps_its_hash()
    {
        var original = new ConfigStateManifest(
            [Pack("gr", "0.2.0", "gr"), Pack("cy-stub", "0.1.0", "cy")], Core, Genesis().Hash, StateCauses.PackActivation);

        var read = ConfigStateManifest.FromJson(original.ToJson());

        read.Hash.ShouldBe(original.Hash);
        read.Packs.OrderBy(p => p.PackId, StringComparer.Ordinal).ToList().ShouldBe([.. original.Packs.OrderBy(p => p.PackId, StringComparer.Ordinal)]);
    }

    [Fact]
    public void The_manifest_refuses_a_second_genesis_a_parentless_activation_and_two_versions_of_one_pack()
    {
        var gr = Pack("gr", "0.2.0", "gr");
        Should.Throw<InvalidOperationException>(() => new ConfigStateManifest([gr], Core, Genesis().Hash, StateCauses.Genesis).Hash);
        Should.Throw<InvalidOperationException>(() => new ConfigStateManifest([gr], Core, null, StateCauses.PackRollback).Hash);
        Should.Throw<InvalidOperationException>(() => Genesis(gr, gr with { Version = "0.1.0" }).Hash);
        Should.Throw<InvalidOperationException>(() => new ConfigStateManifest([gr], Core, null, "WHATEVER").Hash);
    }

    [Fact]
    public void The_content_digest_ignores_the_order_of_the_values_and_changes_with_any_field()
    {
        var values = new GrPackConfiguration().Versions[0].Values;
        var digest = PackVersionContent.DigestOf(values);

        PackVersionContent.DigestOf([.. values.Reverse()]).ShouldBe(digest);
        PackVersionContent.DigestOf([.. values.Skip(1)]).ShouldNotBe(digest);
        PackVersionContent.DigestOf([values[0] with { Value = values[0].Value + "0" }, .. values.Skip(1)]).ShouldNotBe(digest);
        PackVersionContent.DigestOf([values[0] with { LegalStatus = LegalStatus.Unverified }, .. values.Skip(1)]).ShouldNotBe(digest);
        PackVersionContent.DigestOf([values[0] with { SourceRef = values[0].SourceRef + " " }, .. values.Skip(1)]).ShouldNotBe(digest);
        PackVersionContent.DigestOf([values[0] with { ValidFrom = new BusinessDate(2027, 1, 1) }, .. values.Skip(1)]).ShouldNotBe(digest);
    }

    [Fact]
    public void Pack_values_survive_the_stored_json_unchanged_decimals_stay_text()
    {
        var shipped = new GrPackConfiguration().Values;

        var read = PackVersionContent.FromJson(PackVersionContent.ToJson(shipped));

        read.OrderBy(v => v.Key, StringComparer.Ordinal).ThenBy(v => v.ValidFrom).ToList()
            .ShouldBe([.. shipped.OrderBy(v => v.Key, StringComparer.Ordinal).ThenBy(v => v.ValidFrom)]);
        read.Single(v => v.Key == "tax.ipt.rate.general").Value.ShouldBe("0.15");
        PackVersionContent.ToJson(shipped).ToJsonString().ShouldNotContain("0.15,");
    }

    [Fact]
    public void D_SL5_07_GR_0_1_0_is_the_historical_content_and_0_2_0_adds_exactly_the_treatment_rows()
    {
        var gr = new GrPackConfiguration();

        gr.Versions.Select(v => v.Version).ShouldBe(["0.1.0", "0.2.0"]);
        gr.PackVersion.ShouldBe("0.2.0");
        var v1 = gr.Versions[0].Values;
        var v2 = gr.Versions[1].Values;

        // 0.1.0 is the value list as shipped by SL-MKT (commit 98ff52b, no treatment rows); the digest freezes it.
        v1.Count.ShouldBe(9);
        v1.ShouldNotContain(v => v.Key.StartsWith("tax.treatment.", StringComparison.Ordinal));
        PackVersionContent.DigestOf(v1).ToString().ShouldBe("808b5fe0934ab15ec48784a113c4527019260dfe75c06ff6b425815df1869453");

        // 0.2.0 = 0.1.0 + the treatment rows, nothing else added, removed or changed.
        v2.Where(v => !v.Key.StartsWith("tax.treatment.", StringComparison.Ordinal)).ToList().ShouldBe([.. v1]);
        v2.Count(v => v.Key.StartsWith("tax.treatment.", StringComparison.Ordinal)).ShouldBe(9);
        v2.Count.ShouldBe(v1.Count + 9);
        gr.Values.ShouldBe([.. v2]);
    }

    [Fact]
    public void D_SL5_07_CY_0_1_0_carries_no_value_and_0_2_0_the_synthetic_treatment_rows_unchanged()
    {
        var cy = new CyTreatmentRules();

        cy.Versions.Select(v => v.Version).ShouldBe(["0.1.0", "0.2.0"]);
        cy.PackVersion.ShouldBe("0.2.0");
        cy.Versions[0].Values.ShouldBeEmpty();
        cy.Versions[1].Values.Count.ShouldBeGreaterThan(0);
        cy.Values.ShouldBe([.. cy.Versions[1].Values]);
        cy.Values.ShouldAllBe(v => v.Key.StartsWith("tax.treatment.rule.", StringComparison.Ordinal) && v.SourceRef.Contains("SYNTHETIC", StringComparison.Ordinal));

        // The rule version written into every row stays what it was before the pack had versions.
        cy.Values.ShouldAllBe(v => v.Value.Contains("\"ruleVersion\":\"0.1.0\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_genesis_catalogue_of_the_shipped_sources_is_the_newest_version_and_matches_a_catalogue_rebuilt_from_stored_content()
    {
        var sources = new IPackConfigurationSource[] { new GrPackConfiguration() };
        var direct = new ConfigurationCatalogue(sources, Instant.FromUtc(2026, 10, 9));
        var content = sources.Select(s => PackVersionContent.Of(s.PackId, s.PackVersion, s.Country, PackVersionContent.FromJson(PackVersionContent.ToJson(s.Values)))).ToList();
        var rebuilt = new ConfigurationCatalogue(
            new CatalogueState(ConfigStateManifest.Genesis(content, CoreDefaults.Content.Digest), CoreDefaults.Content, content), Instant.FromUtc(2026, 10, 10));

        direct.Hash.ShouldBe(rebuilt.Hash);
        direct.Manifest.Packs.Single().Version.ShouldBe("0.2.0");
        rebuilt.Entries.Count.ShouldBe(direct.Entries.Count);
        rebuilt.Entries.Select(e => (e.Key, e.Node, e.Value, e.LegalStatus)).Order().ShouldBe([.. direct.Entries.Select(e => (e.Key, e.Node, e.Value, e.LegalStatus)).Order()]);
    }

    [Fact]
    public void A_catalogue_refuses_content_that_does_not_match_the_digest_the_manifest_names()
    {
        var gr = new GrPackConfiguration();
        var good = PackVersionContent.Of("gr", "0.2.0", "GR", gr.Values);
        var manifest = ConfigStateManifest.Genesis([good], CoreDefaults.Content.Digest);
        var swapped = good with { Values = gr.Versions[0].Values };
        var otherCore = CoreDefaults.Content with { Values = [.. CoreDefaults.Values.Skip(1)] };

        Should.Throw<InvalidOperationException>(() => new ConfigurationCatalogue(new CatalogueState(manifest, CoreDefaults.Content, [swapped]), Instant.MinValue));
        Should.Throw<InvalidOperationException>(() => new ConfigurationCatalogue(new CatalogueState(manifest, otherCore, [good]), Instant.MinValue));
        Should.Throw<InvalidOperationException>(() => new ConfigurationCatalogue(new CatalogueState(manifest, CoreDefaults.Content, []), Instant.MinValue));
    }
}
