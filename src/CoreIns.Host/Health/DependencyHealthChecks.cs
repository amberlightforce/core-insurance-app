using CoreIns.Host.Hosting;
using CoreIns.Platform.Telemetry;
using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoreIns.Host.Health;

/// <summary>
/// Hangfire storage reachability (NX-OT-07). Optional: a failure is <see cref="HealthStatus.Degraded"/> (HTTP 200), because the schema
/// is installed by the migrate job and may not exist yet on a fresh stack. Only PostgreSQL itself fails readiness.
/// </summary>
internal sealed class HangfireStorageHealthCheck(JobStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var servers = await Task.Run(() => storage.GetMonitoringApi().Servers().Count, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"Hangfire storage is reachable; {servers} server(s) registered.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded("Hangfire storage is not reachable.", ex);
        }
    }
}

/// <summary>
/// Worker-role liveness. From the api: at least one Hangfire server must be registered. In the worker: no sweeper may have gone silent
/// for longer than <c>Health:SweeperStaleAfterSeconds</c> (default 900). Always Degraded, never Unhealthy.
/// </summary>
/// <summary>The role this process runs in, for the health checks.</summary>
internal sealed record HealthRole(AppRole Role);

/// <summary>Worker-role liveness (see below).</summary>
internal sealed class WorkerHealthCheck(HealthRole role, JobStorage storage, IConfiguration configuration, TimeProvider time) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (role.Role == AppRole.Worker)
        {
            var staleSeconds = Math.Max(30, configuration.GetValue("Health:SweeperStaleAfterSeconds", 900));
            var stale = TimeSpan.FromSeconds(staleSeconds);
            var now = time.GetUtcNow();
            var silent = CoreInsMetrics.SweeperHeartbeats.Where(h => now - h.Value > stale).Select(h => h.Key).Order(StringComparer.Ordinal).ToList();
            return silent.Count == 0
                ? HealthCheckResult.Healthy("All sweepers are beating.")
                : HealthCheckResult.Degraded($"Sweepers silent for over {staleSeconds}s: {string.Join(", ", silent)}.");
        }

        try
        {
            var servers = await Task.Run(() => storage.GetMonitoringApi().Servers().Count, cancellationToken).ConfigureAwait(false);
            return servers > 0 ? HealthCheckResult.Healthy("A worker is registered.") : HealthCheckResult.Degraded("No worker has registered with Hangfire.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded("The worker role could not be determined.", ex);
        }
    }
}

/// <summary>
/// Reachability of an optional HTTP dependency (blob storage, Gotenberg). Not configured is Healthy; configured but unreachable is
/// <see cref="HealthStatus.Degraded"/>. For blob storage any HTTP answer (even 400/403 from an unauthenticated probe) proves reachability.
/// </summary>
internal sealed class HttpDependencyHealthCheck(string name, Uri? target, bool anyAnswer, IHttpClientFactory clients) : IHealthCheck
{
    public const string ClientName = "health";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (target is null)
        {
            return HealthCheckResult.Healthy($"{name} is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await clients.CreateClient(ClientName).GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return anyAnswer || response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy($"{name} answered.")
                : HealthCheckResult.Degraded($"{name} answered HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded($"{name} is not reachable.", ex);
        }
    }

    /// <summary><c>DocRender:Url</c> (Gotenberg) probed at <c>/health</c>; null when not configured.</summary>
    public static Uri? GotenbergUri(IConfiguration configuration) =>
        Uri.TryCreate(configuration["DocRender:Url"], UriKind.Absolute, out var uri) ? new Uri(uri, "/health") : null;

    /// <summary><c>Blob:ServiceUri</c>: a service URI (Azure) or an Azurite connection string carrying <c>BlobEndpoint=</c>; null when not configured.</summary>
    public static Uri? BlobUri(IConfiguration configuration)
    {
        var value = configuration["Blob:ServiceUri"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var endpoint = value.Contains('=', StringComparison.Ordinal)
            ? value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == "BlobEndpoint")?[1]
            : value;
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }
}
