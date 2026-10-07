using Hangfire;
using Hangfire.PostgreSql;

namespace CoreIns.Host.Hosting;

/// <summary>Hangfire with PostgreSQL storage (schema <c>hangfire</c>). No jobs are registered yet.</summary>
internal static class JobsExtensions
{
    /// <summary>PostgreSQL schema holding Hangfire's tables. Installed by the migrate job, never at start-up.</summary>
    public const string HangfireSchema = "hangfire";

    /// <summary>Registers Hangfire storage in every mode; the processing server runs only in worker mode.</summary>
    public static IServiceCollection AddCoreInsJobs(this IServiceCollection services, string connectionString, AppRole role)
    {
        services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions
                {
                    SchemaName = HangfireSchema,
                    PrepareSchemaIfNecessary = false,
                }));

        if (role == AppRole.Worker)
        {
            services.AddHangfireServer();
        }

        return services;
    }
}
