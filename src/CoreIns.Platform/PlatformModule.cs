using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Platform;

/// <summary>
/// Composition entry point of the Platform (PRD-14): time service, request context, unit of work, transactional
/// outbox and dispatcher, audit, idempotency, Problem Details, authority framework and configuration runtime.
/// The Host calls <see cref="AddPlatformModule"/> before any business module. Modules then register, from their own
/// <c>Add&lt;X&gt;Module</c>: their DbContext (<c>AddModuleDbContext</c>), commands (<c>AddCommand</c>), event handlers
/// (<c>AddEventHandler</c>), authority types (<see cref="AddAuthorityType"/>) and error definitions
/// (<see cref="AddErrorDefinitions"/>).
/// </summary>
public static class PlatformModule
{
    /// <summary>PostgreSQL schema owned by the platform (outbox, audit, idempotency; configuration, flags and calendars later).</summary>
    public const string Schema = PlatformDbContext.SchemaName;

    /// <summary>All PostgreSQL schemas owned by the platform, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The platform's database for the migrate job: EF Core migrations of <c>plt</c> and the application role's privileges.</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
    [
        new ModuleDatabaseDefinition(
            ModuleCode.PLT,
            Schema,
            connectionString => ModuleDbContextRegistration.CreateForMigration<PlatformDbContext>(connectionString, Schema),
            PlatformGrants),
    ];

    /// <summary>Registers platform services (every app role).</summary>
    public static IServiceCollection AddPlatformModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<StampOptions>().Bind(configuration.GetSection(StampOptions.Section));
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.Section));
        services.AddOptions<AuthorityOptions>().Bind(configuration.GetSection(AuthorityOptions.Section));

        services.TryAddSingleton<IClock>(sp => configuration[ClockConfiguration.ModeKey] is { Length: > 0 }
            ? ClockConfiguration.Create(configuration, sp.GetRequiredService<IHostEnvironment>())
            : SystemClock.Instance);

        // Libraries written against the BCL TimeProvider (e.g. data protection key rings) follow IClock too.
        services.TryAddSingleton<TimeProvider>(sp => new ClockTimeProvider(sp.GetRequiredService<IClock>()));

        services.TryAddScoped<RequestContext>();
        services.TryAddScoped<DbSession>();
        services.TryAddScoped<OutboxStaging>();
        services.AddScoped<ITransactionParticipant>(sp => sp.GetRequiredService<OutboxStaging>());
        services.TryAddScoped<AuditStaging>();
        services.AddScoped<ITransactionParticipant>(sp => sp.GetRequiredService<AuditStaging>());
        services.TryAddScoped<IAuditWriter>(sp => sp.GetRequiredService<AuditStaging>());
        services.TryAddScoped<IEventPublisher, EventPublisher>();

        services.TryAddSingleton<EventHandlerRegistry>();
        services.TryAddSingleton<OutboxProcessor>();
        services.TryAddSingleton<OutboxReplayService>();
        services.TryAddSingleton<AuditChainVerifier>();

        services.TryAddSingleton<ErrorCatalog>();
        services.TryAddSingleton<ProblemDetailsMapper>();
        services.AddExceptionHandler<CoreInsExceptionHandler>();

        services.TryAddSingleton<IAuthorityTypeRegistry, AuthorityTypeRegistry>();
        services.TryAddSingleton<IAuthorityService, ConfiguredAuthorityService>();
        services.TryAddSingleton<IConfigurationResolver, InMemoryConfigurationResolver>();
        return services;
    }

    /// <summary>Runs the outbox dispatcher as a hosted service (worker role only).</summary>
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<OutboxDispatcherService>();
        return services;
    }

    /// <summary>Registers an authority type owned by a module (<c>plt.AuthorityType.register</c> at build time).</summary>
    public static IServiceCollection AddAuthorityType(this IServiceCollection services, AuthorityTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definition);
        services.AddSingleton(definition);
        return services;
    }

    /// <summary>Registers a module's error definitions (status, localized title, retryable) for Problem Details.</summary>
    public static IServiceCollection AddErrorDefinitions(this IServiceCollection services, params ErrorDefinition[] definitions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            services.AddSingleton(definition);
        }

        return services;
    }

    /// <summary>
    /// Privileges of the application role on <c>plt</c> (run by the migrate job after migrating): read/write on the
    /// operational tables, and <b>INSERT and SELECT only</b> on <c>plt.audit_event</c> (D-ARC-15; a trigger also refuses
    /// UPDATE, DELETE and TRUNCATE for every role).
    /// </summary>
    public static IReadOnlyList<string> PlatformGrants(string appRole) =>
    [
        $"REVOKE ALL ON ALL TABLES IN SCHEMA {Schema} FROM {appRole}",
        $"GRANT SELECT, INSERT, UPDATE, DELETE ON {Schema}.outbox_message, {Schema}.aggregate_sequence, {Schema}.processed_event, "
            + $"{Schema}.outbox_dead_letter, {Schema}.event_archive, {Schema}.idempotency_record, {Schema}.audit_chain_head TO {appRole}",
        $"GRANT SELECT, INSERT ON {Schema}.audit_event TO {appRole}",
        $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {Schema} TO {appRole}",
    ];
}
