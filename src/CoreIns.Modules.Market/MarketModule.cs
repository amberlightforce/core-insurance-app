using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CoreIns.Modules.Market;

/// <summary>
/// Composition entry point of the Market module (PRD-17 Multi-market). The Host calls <see cref="AddMarketModule"/>;
/// the migrate job applies <see cref="Databases"/>. Scope so far: the legal-entity registry, the configuration resolver over core defaults
/// and country-pack data, rounding, the Production gate for non-Settled values, tax treatment and calculation, and (SL5-MKT-STATE) the persisted
/// append-only configuration states with the pack-version registry and resolution by any recorded hash.
/// </summary>
public static class MarketModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "mkt";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>
    /// The module database for the migrate job: EF Core migrations of <c>mkt</c>, then least-privilege grants for the app role. The legal-entity
    /// registry and the pack activations move (SELECT, INSERT, UPDATE); the pack versions and the configuration states are append-only
    /// (SELECT, INSERT: a state or a version can never be rewritten, and triggers refuse UPDATE and DELETE for every role). Nothing gets DELETE.
    /// </summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
    [
        new(
            ModuleCode.MKT,
            Schema,
            connectionString => ModuleDbContextRegistration.CreateForMigration<MarketDbContext>(connectionString, Schema),
            appRole =>
            [
                $"REVOKE ALL ON ALL TABLES IN SCHEMA {Schema} FROM {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.legal_entity, {Schema}.pack_activation TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.pack_version, {Schema}.config_state TO {appRole}",
                $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {Schema} TO {appRole}",
            ]),
    ];

    /// <summary>Registers the module's services: DbContext, registry, resolver, in-process contracts, error definitions.</summary>
    public static IServiceCollection AddMarketModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<MarketDbContext>(Schema);

        // The registry replaces the platform's stamp-based directory (D-CON-33).
        services.TryAddSingleton<LegalEntityRegistry>();
        services.RemoveAll<ILegalEntityDirectory>();
        services.AddSingleton<ILegalEntityDirectory, MarketLegalEntityDirectory>();

        // Configuration states (SL5-MKT-STATE, D-SL5-06): persisted, append-only, built from the pack versions registered from the data this
        // release ships (bound by the Host). Genesis is written under an advisory lock when the host starts, or on first read.
        services.TryAddSingleton(sp => new PersistedConfigurationStates(
            sp.GetRequiredService<NpgsqlDataSource>(), sp.GetServices<IPackConfigurationSource>(), sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<PersistedConfigurationStates>>()));
        services.TryAddSingleton<IConfigurationStates>(sp => sp.GetRequiredService<PersistedConfigurationStates>());
        services.TryAddSingleton(sp => new ConfigurationEngine(
            sp.GetRequiredService<IConfigurationStates>(), sp.GetRequiredService<LegalEntityRegistry>(), sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<IClock>()));
        services.AddHostedService<ConfigurationStatesStartup>();
        services.AddScoped<PackRegistryService>();

        // MKT is the configuration authority (D-SLC-15): the platform's resolver, and so the hash pinned per request, is MKT's.
        services.RemoveAll<IConfigurationResolver>();
        services.AddSingleton<IConfigurationResolver, MarketConfigurationResolver>();

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<MarketConfigurationService>();
        services.AddScoped<IMarketConfigurationService>(sp => sp.GetRequiredService<MarketConfigurationService>());
        services.AddScoped<MarketRoundingService>();
        services.AddScoped<IMarketRoundingService>(sp => sp.GetRequiredService<MarketRoundingService>());

        // SPI 4 treatment (SL3-MKT-TREATMENT): rows are pack data in the state, the calculator holds no default. Scoped: it prices under the
        // configuration hash pinned for the unit of work (REQ-MKT-051).
        services.TryAddScoped<MarketTaxCalculator>();
        services.TryAddScoped<ITaxCalculator>(sp => sp.GetRequiredService<MarketTaxCalculator>());

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every MKT-ERR code the slice raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.MKT, "CFG-VALIDATION", 422, "Μη έγκυρο αίτημα ρυθμίσεων", "The configuration request is invalid")
            .Describe("Ελέγξτε τα πεδία του αιτήματος.", "Check the request fields."),
        ErrorDefinition.For(ModuleCode.MKT, "CFG-UNKNOWN-KEY", 422, "Άγνωστο κλειδί ρυθμίσεων", "Unknown configuration key")
            .Describe("Το κλειδί δεν είναι καταχωρισμένο στο μητρώο κλειδιών.", "The key is not registered in the key registry."),
        ErrorDefinition.For(ModuleCode.MKT, "CFG-LEGAL-ENTITY-UNKNOWN", 404, "Άγνωστη νομική οντότητα", "Unknown legal entity")
            .Describe("Η νομική οντότητα δεν υπάρχει στο μητρώο.", "The legal entity is not in the registry."),
        ErrorDefinition.For(ModuleCode.MKT, "CFG-TIMEBASIS-MISSING", 422, "Λείπει η ημερομηνία βάσης χρόνου", "The time-basis date is missing")
            .Describe("Το κλειδί επιλύεται με ημερομηνία φορολογικού γεγονότος· δώστε την στο timeBasisDates.", "The key resolves on the tax-point date; give it in timeBasisDates."),
        ErrorDefinition.For(ModuleCode.MKT, "CFG-NOT-SETTLED", 422, "Η τιμή δεν είναι οριστικοποιημένη", "The value is not Settled")
            .Describe("Στην παραγωγή δεν επιτρέπονται τιμές που δεν έχουν νομική οριστικοποίηση.", "Production refuses values whose legal status is not Settled."),
        ErrorDefinition.For(ModuleCode.MKT, "CFG-HASH-UNKNOWN", 404, "Άγνωστη κατάσταση ρυθμίσεων", "Unknown configuration state")
            .Describe("Το hash ρυθμίσεων δεν είναι γνωστό σε αυτό το stamp.", "The configuration hash is not known to this stamp."),
        ErrorDefinition.For(ModuleCode.MKT, "CFG-RULE-MISSING", 422, "Λείπει κανόνας", "A rule is missing")
            .Describe("Δεν υπάρχει κανόνας· η λειτουργία αποτυγχάνει κλειστά.", "No rule exists; the operation fails closed."),
        ErrorDefinition.For(ModuleCode.MKT, "SPI-VALIDATION", 422, "Μη έγκυρο αίτημα SPI", "The SPI request is invalid")
            .Describe("Ελέγξτε τον τύπο συναλλαγής και την πηγή ακύρωσης.", "Check the transaction kind and the cancellation source."),
        ErrorDefinition.For(ModuleCode.MKT, "SPI-RULE-MISSING", 422, "Λείπει κανόνας SPI", "An SPI rule is missing")
            .Describe("Δεν υπάρχει κανόνας μεταχείρισης φόρου· η λειτουργία αποτυγχάνει κλειστά.", "No tax treatment rule exists; the operation fails closed."),
        ErrorDefinition.For(ModuleCode.MKT, "PACK-NOT-FOUND", 404, "Άγνωστο πακέτο χώρας", "Unknown pack")
            .Describe("Δεν υπάρχει καταχωρισμένο πακέτο με αυτό το αναγνωριστικό.", "No pack with this id is registered."),
        ErrorDefinition.For(ModuleCode.MKT, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
