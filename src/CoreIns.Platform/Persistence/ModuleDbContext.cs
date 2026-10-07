using System.Reflection;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace CoreIns.Platform.Persistence;

/// <summary>
/// Base class of every module's EF Core context: the module's own PostgreSQL schema as default schema, its migrations
/// history table inside that schema, and value converters for SharedKernel types (strongly typed ids, business numbers,
/// <see cref="Instant"/>, <see cref="BusinessDate"/>). A module's context is internal to the module (architecture rule:
/// no module uses another module's DbContext).
/// </summary>
public abstract class ModuleDbContext : DbContext
{
    /// <summary>Name of the EF Core migrations history table, created in the module schema.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>Creates the context.</summary>
    protected ModuleDbContext(DbContextOptions options)
        : base(options)
    {
    }

    /// <summary>The module's schema (e.g. <c>pol</c>).</summary>
    protected abstract string Schema { get; }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        base.OnModelCreating(modelBuilder);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        SharedKernelConventions.Apply(configurationBuilder);
        base.ConfigureConventions(configurationBuilder);
    }
}

/// <summary>EF Core value converters for the SharedKernel value types.</summary>
public static class SharedKernelConventions
{
    /// <summary>Registers converters for every SharedKernel id and text value object, <see cref="Instant"/> and <see cref="BusinessDate"/>.</summary>
    public static void Apply(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        var assembly = typeof(Money).Assembly;
        foreach (var type in assembly.GetExportedTypes().Where(t => t.IsValueType))
        {
            if (Implements(type, typeof(IEntityId<>)))
            {
                configurationBuilder.Properties(type).HaveConversion(typeof(EntityIdConverter<>).MakeGenericType(type));
            }
            else if (Implements(type, typeof(IStringValue<>)))
            {
                configurationBuilder.Properties(type).HaveConversion(typeof(StringValueConverter<>).MakeGenericType(type));
            }
        }

        configurationBuilder.Properties<Instant>().HaveConversion<InstantConverter>();
        configurationBuilder.Properties<BusinessDate>().HaveConversion<BusinessDateConverter>();
    }

    private static bool Implements(Type type, Type openInterface) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface);

}

/// <summary>Strongly typed id ↔ uuid.</summary>
/// <typeparam name="TId">Id type.</typeparam>
public sealed class EntityIdConverter<TId>() : ValueConverter<TId, Guid>(id => id.Value, value => ValueFactories.Id<TId>(value))
    where TId : struct, IEntityId<TId>;

/// <summary>Text value object ↔ text.</summary>
/// <typeparam name="TValue">Value type.</typeparam>
public sealed class StringValueConverter<TValue>() : ValueConverter<TValue, string>(v => v.Value, text => ValueFactories.Text<TValue>(text))
    where TValue : struct, IStringValue<TValue>;

/// <summary><see cref="Instant"/> ↔ timestamptz.</summary>
public sealed class InstantConverter() : ValueConverter<Instant, DateTime>(i => i.ToUtcDateTime(), d => InstantFromDatabase(d))
{
    /// <summary>Npgsql returns timestamptz as a UTC <see cref="DateTime"/>.</summary>
    public static Instant InstantFromDatabase(DateTime value) => Instant.FromUtcDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary><see cref="BusinessDate"/> ↔ date.</summary>
public sealed class BusinessDateConverter() : ValueConverter<BusinessDate, DateOnly>(d => d.Value, v => new BusinessDate(v));

/// <summary>Expression-tree friendly factories (expression trees cannot call static abstract interface members).</summary>
public static class ValueFactories
{
    /// <summary>Wraps a uuid.</summary>
    public static TId Id<TId>(Guid value)
        where TId : struct, IEntityId<TId> => TId.From(value);

    /// <summary>Parses text.</summary>
    public static TValue Text<TValue>(string text)
        where TValue : struct, IStringValue<TValue> => TValue.Parse(text);
}

/// <summary>
/// What the migrate job needs to know about a module's database: its schema, how to build its context for migration
/// with the migrator credential, and which privileges the application role gets on its tables.
/// </summary>
/// <param name="Module">Owning module.</param>
/// <param name="Schema">PostgreSQL schema.</param>
/// <param name="CreateForMigration">Builds the context over a connection string (migrator role).</param>
/// <param name="GrantStatements">The GRANT/REVOKE statements for the application role (idempotent; run after migrating).</param>
public sealed record ModuleDatabaseDefinition(
    ModuleCode Module,
    string Schema,
    Func<string, DbContext> CreateForMigration,
    Func<string, IReadOnlyList<string>> GrantStatements);

/// <summary>Registration of module DbContexts on the scope's <see cref="DbSession"/>.</summary>
public static class ModuleDbContextRegistration
{
    /// <summary>
    /// Registers <typeparamref name="TContext"/> as a scoped service sharing the scope's <see cref="DbSession"/>
    /// connection and transaction. The context's constructor takes <c>DbContextOptions&lt;TContext&gt;</c>.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(schema);
        services.TryAddScoped<DbSession>();
        services.AddScoped(sp =>
        {
            var session = sp.GetRequiredService<DbSession>();
            var options = new DbContextOptionsBuilder<TContext>()
                .UseNpgsql(session.Connection, npgsql => npgsql.MigrationsHistoryTable(ModuleDbContext.MigrationsHistoryTable, schema))
                .Options;
            var context = ActivatorUtilities.CreateInstance<TContext>(sp, options);
            session.Track(context);
            return context;
        });
        return services;
    }

    /// <summary>Options for building a module context over a connection string (migrate job, design time).</summary>
    public static DbContextOptions<TContext> MigrationOptions<TContext>(string connectionString, string schema)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(ModuleDbContext.MigrationsHistoryTable, schema))
            .Options;

    /// <summary>A context instance over a connection string, for the migrate job.</summary>
    public static TContext CreateForMigration<TContext>(string connectionString, string schema)
        where TContext : DbContext =>
        (TContext)Activator.CreateInstance(
            typeof(TContext),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [MigrationOptions<TContext>(connectionString, schema)],
            culture: null)!;

    /// <summary>Registers the data source used by every scope's <see cref="DbSession"/> (no-op when one is registered).</summary>
    public static IServiceCollection AddPlatformDataSource(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        return services;
    }
}
