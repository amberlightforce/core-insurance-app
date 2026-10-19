using CoreIns.CountryPacks.CY.Claims;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Errors;
using Microsoft.Extensions.Configuration;

namespace CoreIns.IntegrationTests.Market;

/// <summary>mkt.Capability.get for cap.clm.friendly_settlement (REQ-MKT-004; GR on, CY off) and the dead-key guard (PITFALLS 37). No database.</summary>
public sealed class CapabilityServiceTests
{
    private static MarketCapabilityService Service(string country = "GR") =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stamp:Country"] = country }).Build());

    [Theory]
    [InlineData("GR", true)]
    [InlineData("CY", false)]
    public async Task Friendly_settlement_is_on_for_GR_and_off_for_CY(string scope, bool enabled)
    {
        var response = await Service().GetAsync(@switch: IFriendlySettlementClearing.CapabilityKey, scope: scope, cancellationToken: TestContext.Current.CancellationToken);
        response.State!.Value.GetProperty("enabled").GetBoolean().ShouldBe(enabled);
        response.State.Value.GetProperty("reversible").GetBoolean().ShouldBeTrue();
        response.Source.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_scope_defaults_to_the_stamp_country()
    {
        var response = await Service("CY").GetAsync(@switch: IFriendlySettlementClearing.CapabilityKey, cancellationToken: TestContext.Current.CancellationToken);
        response.State!.Value.GetProperty("enabled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_switch_is_refused_and_the_validator_rejects_a_key_the_code_never_looks_up()
    {
        await Should.ThrowAsync<DomainException>(async () => await Service().GetAsync(@switch: "cap.unknown.thing", cancellationToken: TestContext.Current.CancellationToken));
        Should.Throw<DomainException>(() => CapabilityCatalogue.Validate(["cap.clm.friendly_settlement", "cap.never.looked_up"]));
        CapabilityCatalogue.Validate([CapabilityCatalogue.FriendlySettlement]);
    }

    [Fact]
    public async Task Cyprus_pack_agrees_with_the_capability_off()
    {
        var spi = new CyFriendlySettlementClearing();
        var facts = new FsClaimFacts
        {
            LegalEntityId = Guid.CreateVersion7(), ClaimId = Guid.CreateVersion7(), AccidentDate = new DateOnly(2026, 10, 1), ProductLine = "MOTOR",
            AccidentInCountry = true, VehicleCount = 2, InsuredFaultPercent = 0m, DamageTypes = ["MATERIAL_DAMAGE"],
            Amount = new SpiMoney(100m, "EUR"), JointReport = false,
        };
        (await spi.EvaluateEligibilityAsync(facts, TestContext.Current.CancellationToken)).Result.ShouldBe(FsEligibilityResult.NotApplicable);
    }
}
