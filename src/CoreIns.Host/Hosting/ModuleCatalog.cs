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

    /// <summary>
    /// Every module database the migrate job migrates (EF Core migrations, then application-role grants), in order.
    /// Business modules add theirs when they get tables.
    /// </summary>
    public static IReadOnlyList<CoreIns.Platform.Persistence.ModuleDatabaseDefinition> Databases { get; } =
    [
        .. PlatformModule.Databases,
        .. PartyModule.Databases,
        .. ProductModule.Databases,
        .. MarketModule.Databases,
        .. PolicyModule.Databases,
        .. BillingModule.Databases,
        .. ComplianceModule.Databases,
    ];

    /// <summary>Module assemblies whose (internal) <c>[ApiController]</c>s the api serves.</summary>
    public static IReadOnlyList<System.Reflection.Assembly> ApiAssemblies { get; } =
    [
        typeof(PartyModule).Assembly,
        typeof(ProductModule).Assembly,
        typeof(MarketModule).Assembly,
        typeof(PolicyModule).Assembly,
        typeof(BillingModule).Assembly,
        typeof(ComplianceModule).Assembly,
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
