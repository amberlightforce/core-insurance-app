using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Domain;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.IntegrationTests.Market.Packs;

public sealed class PackActivationProofTests
{
    private static readonly string[] TreatmentKeys =
    [
        "tax.treatment.rule.TAX.CANCELLATION.Policyholder",
        "tax.treatment.rule.TAX.ENDORSEMENT_CREDIT.ANY",
        "tax.treatment.rule.TAX.RETURN_PREMIUM.ANY",
        "tax.treatment.rule.TAX.REFUND.ANY",
        "tax.treatment.rule.TAX.NEW_BUSINESS.ANY",
        "tax.treatment.rule.TAX.ENDORSEMENT_DEBIT.ANY",
        "tax.treatment.rule.TAX.FEE.ANY",
        "tax.treatment.rule.TAX.VOID.DistanceWithdrawal",
        "tax.treatment.rule.TAX.DISTANCE_WITHDRAWAL_VOID.ANY",
    ];

    [Fact]
    public void REQ_MKT_137_every_frozen_execution_dimension_changes_the_approval_hash()
    {
        var proof = new PackActivationProof("gr", "GR-TEST", "0.2.0", "0.1.0", "ROLLBACK", "Restore the earlier shipped rules",
            new ConfigurationHash(Sha256Hash.ComputeUtf8("parent")), Guid.NewGuid(), Sha256Hash.ComputeUtf8("target"), "USER:dev:maker", null);
        var altered = new[]
        {
            proof with { Pack = "other" }, proof with { Entity = "other" }, proof with { From = "0.3.0" },
            proof with { To = "0.0.0" }, proof with { Kind = "ACTIVATE" }, proof with { Reason = "A different approved business reason" },
            proof with { Parent = new ConfigurationHash(Sha256Hash.ComputeUtf8("changed")) }, proof with { SourceActivation = Guid.NewGuid() },
            proof with { TargetDigest = Sha256Hash.ComputeUtf8("changed") }, proof with { Maker = "USER:other" }, proof with { Principal = "USER:principal" },
        };
        altered.ShouldAllBe(p => p.Hash != proof.Hash);
    }

    [Fact]
    public void REQ_MKT_136_shipped_rollback_removes_exactly_the_treatment_keys_and_reactivation_adds_them()
    {
        var pack = new GrPackConfiguration();
        var earlier = pack.Versions.Single(v => v.Version == "0.1.0").Values;
        var current = pack.Versions.Single(v => v.Version == "0.2.0").Values;
        var rollback = PackActivationProof.KeyDiff(current, earlier);
        // The shipped treatment data includes both withdrawal entry points; the earlier plan's count of eight is stale.
        rollback.Select(d => d.Key).ShouldBe(TreatmentKeys, ignoreOrder: true);
        rollback.ShouldAllBe(d => d.Key.StartsWith("tax.treatment.rule.", StringComparison.Ordinal)
            && d.Change == CoreIns.Modules.Market.Contracts.Api.PackActivationKeyDiff.ChangeValue.Removed);
        PackActivationProof.KeyDiff(earlier, current).ShouldAllBe(d => d.Change == CoreIns.Modules.Market.Contracts.Api.PackActivationKeyDiff.ChangeValue.Added);
        PackActivationProof.KeyDiff(current, current.Reverse().ToList()).ShouldBeEmpty();
    }
}
