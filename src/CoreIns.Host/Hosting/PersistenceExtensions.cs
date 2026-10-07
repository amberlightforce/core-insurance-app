using Npgsql;

namespace CoreIns.Host.Hosting;

/// <summary>PostgreSQL connection settings (INFRASTRUCTURE §5).</summary>
internal static class PersistenceExtensions
{
    /// <summary>Connection string name used by api and worker (database role <c>app</c>).</summary>
    public const string CoreConnectionName = "Core";

    /// <summary>Connection string name used by the migrate job (database role <c>migrator</c>); falls back to <see cref="CoreConnectionName"/>.</summary>
    public const string MigratorConnectionName = "Migrator";

    /// <summary>Returns the application connection string or fails fast with the variable to set.</summary>
    public static string GetRequiredCoreConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString(CoreConnectionName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Connection string '{CoreConnectionName}' is not configured. Set ConnectionStrings__{CoreConnectionName}.");

    /// <summary>Returns the migration connection string (Migrator, else Core) or fails fast.</summary>
    public static string GetRequiredMigratorConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString(MigratorConnectionName) is { Length: > 0 } value
            ? value
            : configuration.GetRequiredCoreConnectionString();

    /// <summary>Registers one pooled <see cref="NpgsqlDataSource"/> for the application role.</summary>
    public static IServiceCollection AddCoreInsDataSource(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        return services;
    }
}
