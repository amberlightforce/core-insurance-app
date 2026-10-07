using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using CoreIns.Host.Hosting;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Host.Database;

/// <summary>
/// The work of <c>APP_ROLE=migrate</c>: creates every module schema, installs Hangfire's storage and grants the
/// application role what it needs. Idempotent. Module tables arrive with EF Core migrations in later packages.
/// </summary>
internal sealed partial class DatabaseMigrator(IConfiguration configuration, ILogger<DatabaseMigrator> logger)
{
    /// <summary>Default name of the application database role (overridable with <c>Database__AppRole</c>).</summary>
    public const string DefaultAppRole = "app";

    /// <summary>Runs the migration and returns the process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var appRole = configuration["Database:AppRole"] is { Length: > 0 } role ? role : DefaultAppRole;
        try
        {
            await MigrateAsync(configuration.GetRequiredMigratorConnectionString(), appRole, logger, cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            LogMigrationFailed(logger, ex);
            return 1;
        }
    }

    /// <summary>Creates schemas, installs Hangfire and grants privileges to <paramref name="appRole"/> if it exists.</summary>
    public static async Task MigrateAsync(string connectionString, string appRole, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string[] schemas = [.. ModuleCatalog.Schemas, JobsExtensions.HangfireSchema];
        foreach (var name in schemas.Append(appRole))
        {
            if (!IdentifierPattern().IsMatch(name))
            {
                throw new InvalidOperationException($"'{name}' is not a valid PostgreSQL identifier for this system.");
            }
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await VerifyExtensionsAsync(connection, cancellationToken).ConfigureAwait(false);
        await VerifyGreekSearchAsync(connection, cancellationToken).ConfigureAwait(false);

        var appRoleExists = await RoleExistsAsync(connection, appRole, cancellationToken).ConfigureAwait(false);
        foreach (var schema in schemas)
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA IF NOT EXISTS {schema}", cancellationToken).ConfigureAwait(false);
            if (appRoleExists)
            {
                await ExecuteAsync(connection, $"GRANT USAGE ON SCHEMA {schema} TO {appRole}", cancellationToken).ConfigureAwait(false);
            }
        }

        PostgreSqlObjectsInstaller.Install(connection, JobsExtensions.HangfireSchema);

        // Module tables: each module's EF Core migrations (history table in its own schema), then its grants.
        foreach (var database in ModuleCatalog.Databases)
        {
            await using (var context = database.CreateForMigration(connectionString))
            {
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            LogModuleMigrated(logger, database.Module, database.Schema);
        }

        if (appRoleExists)
        {
            var hangfire = JobsExtensions.HangfireSchema;
            await ExecuteAsync(connection, $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {hangfire} TO {appRole}", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, $"GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA {hangfire} TO {appRole}", cancellationToken)
                .ConfigureAwait(false);
            foreach (var statement in ModuleCatalog.Databases.SelectMany(database => database.GrantStatements(appRole)))
            {
                await ExecuteAsync(connection, statement, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            LogAppRoleMissing(logger, appRole);
        }

        LogMigrationCompleted(logger, schemas.Length);
    }

    /// <summary>Extensions every environment must have (INFRASTRUCTURE §4 rule 3); created by infra/database/bootstrap.sql.</summary>
    public static IReadOnlyList<string> RequiredExtensions { get; } = ["pg_trgm", "unaccent", "pgcrypto", "btree_gist", "pg_stat_statements"];

    /// <summary>Fails with the list of missing extensions; the migrator role cannot create them itself.</summary>
    private static async Task VerifyExtensionsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var installed = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT extname FROM pg_extension", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                installed.Add(reader.GetString(0));
            }
        }

        var missing = RequiredExtensions.Where(extension => !installed.Contains(extension)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"PostgreSQL extensions missing: {string.Join(", ", missing)}. Run the database bootstrap first "
                + "(locally infra/local/pg-init; on Azure the `bootstrap` job: APP_ROLE=migrate, Migrate__Bootstrap=true).");
        }
    }

    /// <summary>
    /// The Greek search objects of infra/database/greek-search.sql (applied by the bootstrap, which owns schema public):
    /// module searches sort with <c>public.el_gr_ci_ai</c> and SQL tools use <c>public.coreins_search_key</c>.
    /// </summary>
    private static async Task VerifyGreekSearchAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT to_regprocedure('public.coreins_search_key(text)') IS NOT NULL
               AND EXISTS (SELECT 1 FROM pg_collation WHERE collname = 'el_gr_ci_ai' AND collnamespace = 'public'::regnamespace)
            """, connection);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException(
                "Greek search objects missing (public.coreins_search_key, collation public.el_gr_ci_ai). Run the database bootstrap "
                + "(locally: the compose `bootstrap` service or infra/local/pg-init; on Azure: APP_ROLE=migrate, Migrate__Bootstrap=true).");
        }
    }

    private static async Task<bool> RoleExistsAsync(NpgsqlConnection connection, string role, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = $1)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = role });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "DDL cannot be parameterised; identifiers are compile-time module constants validated by IdentifierPattern.")]
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migration completed: {SchemaCount} schemas ensured")]
    private static partial void LogMigrationCompleted(ILogger logger, int schemaCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Module {Module} migrated (schema {Schema})")]
    private static partial void LogModuleMigrated(ILogger logger, CoreIns.SharedKernel.Identifiers.ModuleCode module, string schema);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database role {AppRole} does not exist; privileges were not granted")]
    private static partial void LogAppRoleMissing(ILogger logger, string appRole);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);
}
