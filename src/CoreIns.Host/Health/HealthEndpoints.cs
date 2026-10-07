using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace CoreIns.Host.Health;

/// <summary>Liveness and readiness probes exposed by every container (INFRASTRUCTURE §4 rule 6).</summary>
internal static class HealthEndpoints
{
    /// <summary>Tag for checks that must pass before the container receives traffic or work.</summary>
    public const string ReadyTag = "ready";

    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    /// <summary>Registers the readiness checks.</summary>
    public static IServiceCollection AddCoreInsHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<PostgresReadinessCheck>("postgres", tags: [ReadyTag]);
        return services;
    }

    /// <summary>
    /// <c>/health/live</c> runs no checks (the process answers); <c>/health/ready</c> runs the checks tagged
    /// <see cref="ReadyTag"/>. Both are anonymous so the platform probes can reach them.
    /// </summary>
    public static IEndpointRouteBuilder MapCoreInsHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) })
            .AllowAnonymous();
        return endpoints;
    }
}
