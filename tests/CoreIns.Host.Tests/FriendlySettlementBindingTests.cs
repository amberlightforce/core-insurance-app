using CoreIns.CountryPacks.CY.Claims;
using CoreIns.CountryPacks.GR.Claims;
using CoreIns.Host.Hosting;
using CoreIns.Modules.Market.Contracts.Spi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Host.Tests;

/// <summary>The Friendly Settlement stub (SL4-MKT-FS) is never bound in Production (D-SL4-03); Cyprus always answers NotApplicable.</summary>
public sealed class FriendlySettlementBindingTests
{
    private static ServiceProvider Build(string country, string environment, params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?> { ["Stamp:Country"] = country };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddCountryPacks(configuration, new Environment(environment));
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    public void The_GR_stub_is_bound_only_outside_Production(string environment, bool bound)
    {
        var spi = Build("GR", environment).GetService<IFriendlySettlementClearing>();
        if (bound)
        {
            spi.ShouldBeOfType<FriendlySettlementClearingStub>();
        }
        else
        {
            spi.ShouldBeNull();
        }
    }

    [Fact]
    public async Task The_member_list_is_read_from_configuration()
    {
        var spi = Build("GR", "Development",
            ("FriendlySettlement:Stub:MemberCodes:0", "OWN-INSURER"), ("FriendlySettlement:Stub:MemberCodes:1", "ALPHA-INS"))
            .GetRequiredService<IFriendlySettlementClearing>();
        var facts = new FsClaimFacts
        {
            LegalEntityId = Guid.CreateVersion7(), ClaimId = Guid.CreateVersion7(), AccidentDate = new DateOnly(2026, 10, 1), ProductLine = "MOTOR",
            CounterpartyInsurerCode = "ALPHA-INS", AccidentInCountry = true, VehicleCount = 2, InsuredFaultPercent = 0m,
            DamageTypes = ["MATERIAL_DAMAGE"], Amount = new SpiMoney(1000m, "EUR"), JointReport = true,
        };
        (await spi.EvaluateEligibilityAsync(facts, TestContext.Current.CancellationToken)).Result.ShouldBe(FsEligibilityResult.Eligible);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Cyprus_binds_the_NotApplicable_pack(string environment) =>
        Build("CY", environment).GetRequiredService<IFriendlySettlementClearing>().ShouldBeOfType<CyFriendlySettlementClearing>();

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
