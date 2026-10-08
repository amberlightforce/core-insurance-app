using System.Text.Json;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Services;
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
    public async Task REQ_CLM_072_The_close_guard_blocks_open_reserve_and_pending_payments_only()
    {
        var a = ExposureId.New();
        var b = ExposureId.New();
        var c = ExposureId.New();
        var blocking = CloseGuard.Blocking([new(a, 0m, false), new(b, 0.01m, false), new(c, 0m, true)]);
        blocking.Select(x => x.Exposure).ShouldBe([b, c]);
        blocking[0].Reasons.ShouldBe(["OPEN_RESERVE"]);
        blocking[1].Reasons.ShouldBe(["PAYMENT_PENDING"]);
        (await new NoClaimFinancials().PositionsAsync(ClaimId.New(), [a], TestContext.Current.CancellationToken)).ShouldAllBe(p => p.OpenReserve == 0m && !p.PaymentPending);
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
    public void The_snapshot_adapter_reads_the_announced_POL_shape_at_the_root_or_inside_content()
    {
        var policyId = Guid.NewGuid();
        var insured = Guid.NewGuid();
        var policy = $$"""{"policyId":"{{policyId}}","policyNumber":"POL000000001","productCode":"MOTOR-GR","insuredPartyId":"{{insured}}"}""";
        var typed = JsonDocument.Parse($$$"""
            {"snapshotRef":"S1","validAt":"2027-03-01T10:00:00Z","knownAt":"2027-03-02T10:00:00Z","inForce":true,"status":"IN_FORCE","policy":{{{policy}}},
             "content":{"productVersion":"1.0","segment":{"segmentId":"{{{Guid.Empty}}}"},"coverages":[{"coverageCode":"OD","selected":true},{"coverageCode":"GLASS","selected":false}]}}
            """).RootElement;
        var read = PolicySnapshotAdapter.Parse(typed, Instant.FromUtc(2027, 3, 1, 10), Instant.FromUtc(2027, 3, 2, 10));
        read.Outcome.ShouldBe(SnapshotReadOutcome.Found);
        read.Facts!.SnapshotRef.ShouldBe("S1");
        read.Facts.PolicyId.ShouldBe(policyId);
        read.Facts.InsuredPartyId.ShouldBe(insured);
        read.Facts.CoverageCodes.ShouldBe(["OD"]);
        read.Facts.ProductVersion.ShouldBe("1.0");

        var untyped = JsonDocument.Parse($$$"""{"snapshotRef":"S2","content":{"inForce":false,"notInForceReason":"NO_TERM_AT_INSTANT","policy":{{{policy}}}}}""").RootElement;
        var fallback = PolicySnapshotAdapter.Parse(untyped, Instant.FromUtc(2027, 3, 1), Instant.FromUtc(2027, 3, 2));
        fallback.Facts!.InForce.ShouldBeFalse();
        fallback.Facts.NotInForceReason.ShouldBe("NO_TERM_AT_INSTANT");
        fallback.Facts.CoverageCodes.ShouldBeEmpty();

        PolicySnapshotAdapter.Parse(JsonDocument.Parse("""{"content":{}}""").RootElement, Instant.FromUtc(2027, 3, 1), Instant.FromUtc(2027, 3, 2))
            .Outcome.ShouldBe(SnapshotReadOutcome.Unverified);
    }

    private static PolicySnapshotFacts Facts(bool inForce, params string[] coverages) =>
        new("S", Instant.FromUtc(2027, 1, 1), Instant.FromUtc(2027, 1, 2), inForce, null, null, Guid.NewGuid(), "POL1", "MOTOR-GR", "1.0", Guid.NewGuid(), null, coverages);
}
