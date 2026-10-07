using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Migration;

/// <summary>
/// Composition entry point of the Migration module (PRD-16 Migration).
/// The Host calls <see cref="AddMigrationModule"/>; the module registers its own services here.
/// </summary>
public static class MigrationModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "mig";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>Registers the module's services. The module has no services yet (placeholder until its feature package).</summary>
    public static IServiceCollection AddMigrationModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
