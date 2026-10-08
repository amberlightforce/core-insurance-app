using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Services;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Host.Tests;

/// <summary>D-SL2-05: the PTY sanctions-screening stub is never bound in Production (as the fiscal stub); Production fails closed.</summary>
public sealed class ScreeningStubBindingTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Outside_Production_the_stub_is_bound(string environment)
    {
        var services = new ServiceCollection();
        services.AddPartyScreening(new Environment(environment));

        services.Single(d => d.ServiceType == typeof(IPartyScreeningService)).ImplementationType!.Name.ShouldBe("StubPartyScreeningService");
    }

    [Fact]
    public async Task In_Production_the_stub_refuses_to_register_and_screening_fails_closed()
    {
        var production = new Environment("Production");

        var refused = Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddStubPartyScreening(production));
        refused.Message.ShouldContain("never bound in Production");

        var services = new ServiceCollection();
        services.AddPartyScreening(production);
        services.Single(d => d.ServiceType == typeof(IPartyScreeningService)).ImplementationType!.Name.ShouldBe("UnavailablePartyScreeningService");
        services.ShouldNotContain(d => d.ServiceType.GenericTypeArguments.Any(t => t.Name == "ScreenStub"));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var error = await Should.ThrowAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IPartyScreeningService>().ScreenAsync(
            new ScreeningScreenRequest { PartyId = new PartyId(Guid.NewGuid()), CallerRef = new ObjectRef(ModuleCode.BIL, "Disbursement", "d-1") },
            CommandOptions.New(),
            TestContext.Current.CancellationToken));
        error.Error.Code.Value.ShouldBe("PTY-ERR-NOT-AVAILABLE");
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
