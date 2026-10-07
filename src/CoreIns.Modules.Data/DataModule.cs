using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Data;

/// <summary>
/// Composition entry point of the Data module (PRD-15 Data & analytics).
/// The Host calls <see cref="AddDataModule"/>; the module registers its own services here.
/// </summary>
public static class DataModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "dat";

    /// <summary>Raw (bronze) reporting layer, loaded from outbox events and control-total feeds.</summary>
    public const string ReportingRawSchema = "rpt_raw";

    /// <summary>Conformed (silver) reporting layer.</summary>
    public const string ReportingConformedSchema = "rpt_conformed";

    /// <summary>Report marts (gold) used by dashboards and regulatory outputs.</summary>
    public const string ReportingMartSchema = "rpt_mart";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema, ReportingRawSchema, ReportingConformedSchema, ReportingMartSchema];

    /// <summary>Registers the module's services. The module has no services yet (placeholder until its feature package).</summary>
    public static IServiceCollection AddDataModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
