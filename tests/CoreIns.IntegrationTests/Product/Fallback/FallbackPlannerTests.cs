using CoreIns.Modules.Product.Domain;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.IntegrationTests.Product.Fallback;

public sealed class FallbackPlannerTests
{
    private static readonly BusinessDate From = new(2026, 1, 1);
    private static readonly BusinessDate At = new(2026, 10, 9);

    private static VersionFacts Version(int minor, params string[] channels) => new(
        ProductVersionId.New(), new ProductVersionNumber(1, minor), FallbackPlanner.Locked, false, channels,
        DateRange.Open(From), DateRange.Open(From), new string('a', 64), false);

    [Fact]
    public void The_highest_eligible_predecessor_is_selected_even_when_a_nearer_version_has_another_channel_scope()
    {
        var source = Version(0, "WEB_DIRECT", "BROKER");
        var nearerOtherScope = Version(2, "WEB_DIRECT");
        var defective = Version(3, "BROKER", "WEB_DIRECT");

        var result = FallbackPlanner.Plan([source, nearerOtherScope, defective], defective.Number, At);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Source.Id.ShouldBe(source.Id);
        // The slice calls this the next minor version: allocation is monotonic rather than filling the unused 1.1 hole.
        result.Value.NewVersion.ShouldBe(new ProductVersionNumber(1, 4));
    }

    [Fact]
    public void A_predecessor_from_another_channel_scope_cannot_be_used_when_no_eligible_source_exists()
    {
        var defective = Version(1, "WEB_DIRECT");

        var result = FallbackPlanner.Plan([Version(0, "BROKER"), defective], defective.Number, At);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.Value.ShouldBe("PFC-ERR-FALLBACK-SOURCE");
    }
}
