using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.Modules.Market;

/// <summary>
/// Composition entry point of the Market module (PRD-17 Multi-market). The Host calls <see cref="AddMarketModule"/>;
/// the migrate job applies <see cref="Databases"/>. Slice scope (SL-MKT): the legal-entity registry, the configuration
/// resolver over core defaults and country-pack data, rounding, and the Production gate for non-Settled values.
/// </summary>
public static class MarketModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "mkt";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: EF Core migrations of <c>mkt</c>, then SELECT/INSERT/UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<MarketDbContext>(ModuleCode.MKT, Schema)];

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

        // Configuration state: core defaults plus the data of every bound pack source (bound by the Host).
        services.TryAddSingleton(sp => new ConfigurationCatalogue(sp.GetServices<IPackConfigurationSource>(), sp.GetRequiredService<IClock>().Now));
        services.TryAddSingleton<ConfigurationEngine>();

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<MarketConfigurationService>();
        services.AddScoped<IMarketConfigurationService>(sp => sp.GetRequiredService<MarketConfigurationService>());
        services.AddScoped<MarketRoundingService>();
        services.AddScoped<IMarketRoundingService>(sp => sp.GetRequiredService<MarketRoundingService>());

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
        ErrorDefinition.For(ModuleCode.MKT, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
