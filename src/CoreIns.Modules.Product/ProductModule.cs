using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Product;

/// <summary>
/// Composition entry point of the Product module (PRD-02 Product factory).
/// The Host calls <see cref="AddProductModule"/>; the module registers its own services here.
/// </summary>
public static class ProductModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "pfc";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>Registers the module's services. The module has no services yet (placeholder until its feature package).</summary>
    public static IServiceCollection AddProductModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
