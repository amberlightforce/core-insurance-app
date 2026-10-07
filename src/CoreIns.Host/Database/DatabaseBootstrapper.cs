using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Npgsql;

namespace CoreIns.Host.Database;

/// <summary>
/// The bootstrap phase of <c>APP_ROLE=migrate</c> (<c>Migrate__Bootstrap=true</c>): with the server administrator
/// credential it creates the application database if missing and runs <c>infra/database/bootstrap.sql</c>
/// (roles <c>app</c> and <c>migrator</c>, database privileges, extensions). Idempotent; run before every migration
/// on Azure. Locally, pg-init runs the same script.
/// </summary>
internal sealed partial class DatabaseBootstrapper(IConfiguration configuration, ILogger<DatabaseBootstrapper> logger)
{
    /// <summary>Configuration key that switches the migrate job into the bootstrap phase.</summary>
    public const string BootstrapKey = "Migrate:Bootstrap";

    /// <summary>Default application database name (overridable with <c>Database__Name</c>).</summary>
    public const string DefaultDatabaseName = "coreins";

    private const string ScriptResource = "CoreIns.Host.Database.bootstrap.sql";

    /// <summary>Runs the bootstrap and returns the process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var admin = Required("ConnectionStrings:Admin", "ConnectionStrings__Admin");
            var appPassword = Required("Database:AppPassword", "Database__AppPassword");
            var migratorPassword = Required("Database:MigratorPassword", "Database__MigratorPassword");
            var database = configuration["Database:Name"] is { Length: > 0 } name ? name : DefaultDatabaseName;

            await BootstrapAsync(admin, database, appPassword, migratorPassword, cancellationToken).ConfigureAwait(false);
            LogBootstrapCompleted(logger, database);
            return 0;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            LogBootstrapFailed(logger, ex);
            return 1;
        }
    }

    /// <summary>
    /// Creates <paramref name="database"/> if it does not exist (connected to the admin connection's own database),
    /// then runs the bootstrap script inside it.
    /// </summary>
    public static async Task BootstrapAsync(
        string adminConnectionString, string database, string appPassword, string migratorPassword, CancellationToken cancellationToken)
    {
        if (!DatabaseName().IsMatch(database))
        {
            throw new InvalidOperationException($"'{database}' is not a valid database name for this system.");
        }

        await using (var maintenance = NpgsqlDataSource.Create(adminConnectionString))
        await using (var connection = await maintenance.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1)", connection);
            exists.Parameters.Add(new NpgsqlParameter { Value = database });
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                await ExecuteAsync(connection, $"CREATE DATABASE {database} ENCODING 'UTF8' TEMPLATE template0", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var target = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database }.ConnectionString;
        await using var dataSource = NpgsqlDataSource.Create(target);
        await using var session = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var settings = new NpgsqlCommand(
            "SELECT set_config('coreins.app_password', $1, false), set_config('coreins.migrator_password', $2, false)", session))
        {
            settings.Parameters.Add(new NpgsqlParameter { Value = appPassword });
            settings.Parameters.Add(new NpgsqlParameter { Value = migratorPassword });
            await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(session, ReadScript(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The embedded copy of infra/database/bootstrap.sql.</summary>
    public static string ReadScript()
    {
        using var stream = typeof(DatabaseBootstrapper).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException($"Embedded resource {ScriptResource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string Required(string key, string variable) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Bootstrap needs {variable}.");

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Repository script or DDL with a validated identifier; passwords travel as bind parameters.")]
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseName();

    [LoggerMessage(Level = LogLevel.Information, Message = "Database bootstrap completed for {Database}")]
    private static partial void LogBootstrapCompleted(ILogger logger, string database);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database bootstrap failed")]
    private static partial void LogBootstrapFailed(ILogger logger, Exception exception);
}
