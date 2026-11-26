using CoreIns.Host.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoreIns.Host.Health;

/// <summary>Liveness and readiness probes exposed by every container (INFRASTRUCTURE §4 rule 6).</summary>
internal static class HealthEndpoints
{
    /// <summary>Tag for checks that must pass before the container receives traffic or work.</summary>
    public const string ReadyTag = "ready";

    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    /// <summary>
    /// Registers the readiness checks. PostgreSQL is the only hard dependency (Unhealthy fails <c>/health/ready</c>); every other check
    /// reports at most <see cref="HealthStatus.Degraded"/>, which is still HTTP 200, so optional pieces absent from local Docker or the
    /// E2E stacks never hold the stack back (NX-OT-07).
    /// </summary>
    public static IServiceCollection AddCoreInsHealthChecks(this IServiceCollection services, AppRole role = AppRole.Api)
    {
        services.AddHttpClient(HttpDependencyHealthCheck.ClientName);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new HealthRole(role));
        services.AddHealthChecks()
            .AddCheck<PostgresReadinessCheck>("postgres", tags: [ReadyTag])
            .AddCheck<HangfireStorageHealthCheck>("hangfire", HealthStatus.Degraded, tags: [ReadyTag])
            .AddCheck<WorkerHealthCheck>("worker", HealthStatus.Degraded, tags: [ReadyTag])
            .Add(new HealthCheckRegistration(
                "blob",
                sp => new HttpDependencyHealthCheck("Blob storage", HttpDependencyHealthCheck.BlobUri(sp.GetRequiredService<IConfiguration>()), true, sp.GetRequiredService<IHttpClientFactory>()),
                HealthStatus.Degraded,
                [ReadyTag]))
            .Add(new HealthCheckRegistration(
                "gotenberg",
                sp => new HttpDependencyHealthCheck("Gotenberg", HttpDependencyHealthCheck.GotenbergUri(sp.GetRequiredService<IConfiguration>()), false, sp.GetRequiredService<IHttpClientFactory>()),
                HealthStatus.Degraded,
                [ReadyTag]));
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
