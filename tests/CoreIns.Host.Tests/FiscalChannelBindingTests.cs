using CoreIns.CountryPacks.GR.Fiscal;
using CoreIns.Host.Hosting;
using CoreIns.Modules.Market.Contracts.Spi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Host.Tests;

/// <summary>The myDATA stub fiscal channel (SL-BIL) is never bound in Production; elsewhere it is (REQ-CMP-001 stub).</summary>
public sealed class FiscalChannelBindingTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    public void The_stub_fiscal_channel_is_bound_only_outside_Production(string environment, bool bound)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stamp:Country"] = "GR" }).Build();
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddCountryPacks(configuration, new Environment(environment));

        var channel = services.BuildServiceProvider().GetService<IFiscalDocumentChannel>();
        if (bound)
        {
            channel.ShouldBeOfType<MyDataStubFiscalChannel>();
        }
        else
        {
            channel.ShouldBeNull();
        }
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
