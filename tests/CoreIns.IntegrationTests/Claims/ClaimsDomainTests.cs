using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Services;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.IntegrationTests.Claims;

/// <summary>Unit tests of the CLM domain rules (no database): state model, close guard, coverage indications, snapshot reading.</summary>
public sealed class ClaimsDomainTests
{
    [Theory]
    [InlineData("Draft", "Submit", "New")]
    [InlineData("New", "HandlerAction", "InProgress")]
    [InlineData("InProgress", "HandlerAction", "InProgress")]
    [InlineData("New", "Close", "Closed")]
    [InlineData("InProgress", "Close", "Closed")]
    [InlineData("Settled", "Close", "Closed")]
    [InlineData("Closed", "Reopen", "InProgress")]
    public void REQ_CLM_071_Declared_transitions_fire(string from, string trigger, string to) =>
        ClaimStateModel.Fire(Enum.Parse<ClaimState>(from), Enum.Parse<ClaimTrigger>(trigger)).Value.ShouldBe(Enum.Parse<ClaimState>(to));

    [Theory]
    [InlineData("Closed", "Close")]
    [InlineData("Closed", "HandlerAction")]
    [InlineData("Draft", "Close")]
    [InlineData("Draft", "HandlerAction")]
    [InlineData("New", "Submit")]
    public void REQ_CLM_071_Undeclared_transitions_are_CLM_ERR_ILLEGAL_TRANSITION(string from, string trigger) =>
        ClaimStateModel.Fire(Enum.Parse<ClaimState>(from), Enum.Parse<ClaimTrigger>(trigger)).Error!.Code.Value.ShouldBe("CLM-ERR-ILLEGAL-TRANSITION");

    [Fact]
    public void Stored_status_pairs_round_trip()
    {
        foreach (var state in Enum.GetValues<ClaimState>())
        {
            var (status, sub) = ClaimStates.ToColumns(state);
            ClaimStates.FromColumns(status, sub).ShouldBe(state);
        }

        ClaimStates.ToColumns(ClaimState.InProgress).ShouldBe(("OPEN", "IN_PROGRESS"));
        ClaimStates.ToColumns(ClaimState.Closed).ShouldBe(("CLOSED", null));
    }

    [Fact]
    public void REQ_CLM_072_The_close_guard_blocks_open_reserve_and_pending_payments_only()
    {
        var a = ExposureId.New();
        var b = ExposureId.New();
        var c = ExposureId.New();
        var blocking = CloseGuard.Blocking([new(a, 0m, false), new(b, 0.01m, false), new(c, 0m, true)]);
        blocking.Select(x => x.Exposure).ShouldBe([b, c]);
        blocking[0].Reasons.ShouldBe(["OPEN_RESERVE"]);
        blocking[1].Reasons.ShouldBe(["PAYMENT_PENDING"]);
    }

    [Fact]
    public void REQ_CLM_048_049_Coverage_indications()
    {
        var inForce = Facts(true, "OD", "MTPL");
        CoverageRules.Indicate(inForce, "OD").ShouldBe(CoverageIndicationCode.Covered);
        CoverageRules.Indicate(inForce, "THEFT").ShouldBe(CoverageIndicationCode.NotCovered);
        CoverageRules.Indicate(Facts(false), "OD").ShouldBe(CoverageIndicationCode.InQuestion);
    }

    [Fact]
    public void The_snapshot_adapter_maps_the_typed_POL_snapshot_and_refuses_an_answer_for_another_policy()
    {
        var policyId = Guid.NewGuid();
        var notInForce = new SnapshotGetResponse
        {
            SnapshotRef = "S2", ValidAt = Instant.FromUtc(2027, 3, 1), KnownAt = Instant.FromUtc(2027, 3, 2), InForce = false,
            NotInForceReason = SnapshotGetResponse.NotInForceReasonValue.NoTermAtInstant,
            Policy = new SnapshotPolicy
            {
                PolicyId = new PolicyId(policyId), PolicyNumber = PolicyNumber.Parse("POL000000001"), ProductCode = "MOTOR-GR",
                InsuredPartyId = Guid.NewGuid(), LegalEntity = "GR-TEST", Jurisdiction = "GR",
            },
        };
        var read = PolicySnapshotAdapter.Map(notInForce, policyId);
        read.Outcome.ShouldBe(SnapshotReadOutcome.Found);
        read.Facts!.InForce.ShouldBeFalse();
        read.Facts.NotInForceReason.ShouldBe("NO_TERM_AT_INSTANT");
        read.Facts.CoverageCodes.ShouldBeEmpty();
        read.Facts.PolicyNumber.ShouldBe("POL000000001");

        PolicySnapshotAdapter.Map(notInForce, Guid.NewGuid()).Outcome.ShouldBe(SnapshotReadOutcome.Unverified);
    }

    private static PolicySnapshotFacts Facts(bool inForce, params string[] coverages) =>
        new("S", Instant.FromUtc(2027, 1, 1), Instant.FromUtc(2027, 1, 2), inForce, null, null, Guid.NewGuid(), "POL1", "MOTOR-GR", "1.0", Guid.NewGuid(), null, coverages);
}
