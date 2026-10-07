using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace CoreIns.Host.Health;

/// <summary>Ready only when PostgreSQL answers a trivial query with the application role.</summary>
internal sealed class PostgresReadinessCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "PostgreSQL is not reachable.", ex);
        }
    }
}
