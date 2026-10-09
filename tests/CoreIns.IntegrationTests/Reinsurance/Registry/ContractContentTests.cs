using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.IntegrationTests.Reinsurance.Registry;

/// <summary>The pure rules and the content hash of a treaty (REQ-RI-037, -038, -046, -047, -057).</summary>
public sealed class ContractContentTests
{
    private static readonly Guid Lead = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid Follow = Guid.Parse("22222222-2222-7222-8222-222222222222");

    private static ContractContent Valid() => new(
        ContractRules.XolPerRisk, 2026, "EUR", new BusinessDate(2026, 1, 1), new BusinessDate(2027, 1, 1), ["MOTOR", "HOME"], ["OD", "MTPL"], true, false, ContractRules.RealisedOnly,
        [new ContentLayer(2, 1_000_000m, 1_000_000m, 0m, null), new ContentLayer(1, 500_000m, 500_000m, 0m, 1_500_000m)],
        [new ContentLine(Follow, null, 40m, false), new ContentLine(Lead, null, 60m, true)], 100m);

    [Fact]
    public void A_valid_treaty_passes()
    {
        ContractRules.Validate(Valid()).ShouldBeNull();
    }

    [Fact]
    public void REQ_RI_046_047_Signed_lines_must_equal_the_placed_share_with_exactly_one_lead()
    {
        ContractRules.Validate(Valid() with { PlacedPct = 90m })!.Code.Name.ShouldBe("SIGNED-LINES");
        ContractRules.Validate(Valid() with { Lines = [new ContentLine(Lead, null, 60m, true), new ContentLine(Follow, null, 40m, true)] })!.Code.Name.ShouldBe("SIGNED-LINES");
        ContractRules.Validate(Valid() with { Lines = [new ContentLine(Lead, null, 60m, false), new ContentLine(Follow, null, 40m, false)] })!.Code.Name.ShouldBe("SIGNED-LINES");
        ContractRules.Validate(Valid() with { Lines = [new ContentLine(Lead, null, 50m, true), new ContentLine(Lead, null, 50m, false)] })!.Code.Name.ShouldBe("SIGNED-LINES");
        ContractRules.Validate(Valid() with { PlacedPct = 80m, Lines = [new ContentLine(Lead, null, 50m, true), new ContentLine(Follow, null, 30m, false)] }).ShouldBeNull();

        // Decimal arithmetic is exact: thirds do not add up to one hundred.
        ContractRules.Validate(Valid() with { Lines = [new ContentLine(Lead, null, 33.333333m, true), new ContentLine(Follow, null, 66.666666m, false)] })!.Code.Name.ShouldBe("SIGNED-LINES");
        ContractRules.Validate(Valid() with { Lines = [new ContentLine(Lead, null, 33.333333m, true), new ContentLine(Follow, null, 66.666667m, false)] }).ShouldBeNull();
    }

    [Fact]
    public void D_SL4_04_Only_XoL_per_risk_in_EUR_with_realised_only_inuring()
    {
        ContractRules.Validate(Valid() with { ContractType = "QUOTA_SHARE" })!.Code.Name.ShouldBe("VALIDATION");
        ContractRules.Validate(Valid() with { Currency = "USD" })!.Code.Name.ShouldBe("VALIDATION");
        ContractRules.Validate(Valid() with { RecoveriesInure = "INCLUDING_RESERVES" })!.Code.Name.ShouldBe("VALIDATION");
        ContractRules.Validate(Valid() with { ContractYear = 2025 })!.Code.Name.ShouldBe("VALIDATION");
        ContractRules.Validate(Valid() with { ProductCodes = ["lower"] })!.Code.Name.ShouldBe("VALIDATION");
        ContractRules.Validate(Valid() with { CoverageCodes = ["OD", "OD"] })!.Code.Name.ShouldBe("VALIDATION");
        ContractRules.Validate(Valid() with { Layers = [new ContentLayer(1, 0m, 100.00001m, 0m, null)] })!.Code.Name.ShouldBe("VALIDATION");
        ContractRules.Validate(Valid() with { Layers = [new ContentLayer(1, 0m, 100m, 0m, null), new ContentLayer(1, 100m, 100m, 0m, null)] })!.Code.Name.ShouldBe("VALIDATION");
    }

    [Fact]
    public void REQ_RI_057_The_hash_is_stable_across_order_and_scale_and_changes_with_any_term()
    {
        var id = RiContractId.New();
        var baseline = Valid().Hash(id, 1);
        var reordered = Valid() with
        {
            ProductCodes = ["HOME", "MOTOR"],
            Layers = [new ContentLayer(1, 500_000.0000m, 500_000.00m, 0m, 1_500_000m), new ContentLayer(2, 1_000_000m, 1_000_000m, 0m, null)],
            Lines = [new ContentLine(Lead, null, 60.000000m, true), new ContentLine(Follow, null, 40m, false)],
            PlacedPct = 100.000000m,
        };
        reordered.Hash(id, 1).ShouldBe(baseline);

        var changed = new[]
        {
            Valid() with { PlacedPct = 99m },
            Valid() with { ValidTo = new BusinessDate(2027, 1, 2) },
            Valid() with { AlaeIncluded = false },
            Valid() with { StatutoryInterestIncluded = true },
            Valid() with { CoverageCodes = ["OD"] },
            Valid() with { Layers = [new ContentLayer(2, 1_000_000m, 1_000_000m, 0m, null), new ContentLayer(1, 500_000m, 500_000m, 0m, 1_500_000.01m)] },
            Valid() with { Lines = [new ContentLine(Follow, null, 40m, true), new ContentLine(Lead, null, 60m, false)] },
            Valid() with { Lines = [new ContentLine(Follow, Guid.NewGuid(), 40m, false), new ContentLine(Lead, null, 60m, true)] },
        };
        changed.Select(c => c.Hash(id, 1)).Append(baseline).Distinct().Count().ShouldBe(changed.Length + 1);

        // Bound to the contract and the version: it cannot be replayed elsewhere.
        Valid().Hash(RiContractId.New(), 1).ShouldNotBe(baseline);
        Valid().Hash(id, 2).ShouldNotBe(baseline);
    }
}
