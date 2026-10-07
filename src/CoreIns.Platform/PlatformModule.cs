using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Platform;

/// <summary>
/// Composition entry point of the Platform (PRD-14): outbox, audit, configuration, authority.
/// The Host calls <see cref="AddPlatformModule"/> before any business module.
/// </summary>
public static class PlatformModule
{
    /// <summary>PostgreSQL schema owned by the platform (outbox, audit, configuration, flags, calendars).</summary>
    public const string Schema = "plt";

    /// <summary>All PostgreSQL schemas owned by the platform, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>Registers platform services. None yet (placeholder until F-1b).</summary>
    public static IServiceCollection AddPlatformModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
