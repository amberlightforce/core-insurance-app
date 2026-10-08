using CoreIns.Modules.Claims.Authority;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Claims;

/// <summary>
/// Composition entry point of the Claims module (PRD-07 Claims).
/// The Host calls <see cref="AddClaimsModule"/>; the module registers its own services here.
/// </summary>
public static class ClaimsModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "clm";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>Registers the module's services: so far the claims authority types (SL2-PLT, D-SL2-03).</summary>
    public static IServiceCollection AddClaimsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddClaimsAuthorityTypes();
        return services;
    }
}
