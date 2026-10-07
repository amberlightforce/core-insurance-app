using CoreIns.Modules.Billing;
using CoreIns.Modules.Channels;
using CoreIns.Modules.Claims;
using CoreIns.Modules.Compliance;
using CoreIns.Modules.Data;
using CoreIns.Modules.Documents;
using CoreIns.Modules.Finance;
using CoreIns.Modules.Market;
using CoreIns.Modules.Migration;
using CoreIns.Modules.Party;
using CoreIns.Modules.Policy;
using CoreIns.Modules.Product;
using CoreIns.Modules.Rating;
using CoreIns.Modules.Reinsurance;
using CoreIns.Modules.Underwriting;
using CoreIns.Modules.Work;
using CoreIns.Platform;

namespace CoreIns.Host.Hosting;

/// <summary>The composition root's list of modules: their registration hooks and their PostgreSQL schemas.</summary>
internal static class ModuleCatalog
{
    /// <summary>Every schema owned by the platform and the business modules, in creation order.</summary>
    public static IReadOnlyList<string> Schemas { get; } =
    [
        .. PlatformModule.Schemas,
        .. PartyModule.Schemas,
        .. ProductModule.Schemas,
        .. RatingModule.Schemas,
        .. UnderwritingModule.Schemas,
        .. PolicyModule.Schemas,
        .. BillingModule.Schemas,
        .. ClaimsModule.Schemas,
        .. ReinsuranceModule.Schemas,
        .. FinanceModule.Schemas,
        .. DocumentsModule.Schemas,
        .. ComplianceModule.Schemas,
        .. ChannelsModule.Schemas,
        .. WorkModule.Schemas,
        .. DataModule.Schemas,
        .. MigrationModule.Schemas,
        .. MarketModule.Schemas,
    ];

    /// <summary>Calls every module's registration hook. The platform registers first.</summary>
    public static IServiceCollection AddCoreInsModules(this IServiceCollection services, IConfiguration configuration) =>
        services
            .AddPlatformModule(configuration)
            .AddPartyModule(configuration)
            .AddProductModule(configuration)
            .AddRatingModule(configuration)
            .AddUnderwritingModule(configuration)
            .AddPolicyModule(configuration)
            .AddBillingModule(configuration)
            .AddClaimsModule(configuration)
            .AddReinsuranceModule(configuration)
            .AddFinanceModule(configuration)
            .AddDocumentsModule(configuration)
            .AddComplianceModule(configuration)
            .AddChannelsModule(configuration)
            .AddWorkModule(configuration)
            .AddDataModule(configuration)
            .AddMigrationModule(configuration)
            .AddMarketModule(configuration);
}
