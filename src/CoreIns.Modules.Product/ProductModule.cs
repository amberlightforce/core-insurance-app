using CoreIns.Modules.Product.Commands;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Persistence;
using CoreIns.Modules.Product.Queries;
using CoreIns.Modules.Product.Services;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Product;

/// <summary>
/// Composition entry point of the Product module (PRD-02 Product factory). The Host calls <see cref="AddProductModule"/>;
/// the migrate job applies <see cref="Databases"/>. SL-PFC slice: one motor product as data, resolved by date
/// (see <c>Seed/motor-gr.product.json</c> and <see cref="ProductSeeds"/>).
/// </summary>
public static class ProductModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "pfc";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: EF Core migrations of <c>pfc</c>, then SELECT/INSERT/UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<ProductDbContext>(ModuleCode.PFC, Schema)];

    /// <summary>Registers the module's services: DbContext, the import command, queries, in-process contracts, error definitions.</summary>
    public static IServiceCollection AddProductModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<ProductDbContext>(Schema);

        // Reads: plain scoped queries (never pipeline commands: a caller's Idempotency-Key must not store a result).
        services.AddSingleton<ArtifactCache>();
        services.AddScoped<ProductReader>();
        services.AddScoped<CatalogueQueries>();

        // The one state-changing path: import a product source document (the seed and, later, the authoring workflow).
        services.AddScoped<IValidator<ImportProductVersion>, ImportProductVersionValidator>();
        services.AddCommandAuditor<ImportProductVersion, ProductVersionImportResponse, ImportProductVersionAuditor>();
        services.AddCommand<ImportProductVersion, ProductVersionImportResponse, ImportProductVersionHandler>(CommandDescriptor.For("pfc.ProductVersion.import"));

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<IProductProductVersionService, ProductProductVersionService>();
        services.AddScoped<IProductCatalogueService, ProductCatalogueService>();
        services.AddScoped<IProductChargeTypeService, ProductChargeTypeService>();
        services.AddScoped<IProductQuestionSetService, ProductQuestionSetService>();
        services.AddScoped<IProductArtifactService, ProductArtifactService>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every PFC-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.PFC, "UNKNOWN-PRODUCT", 422, "Άγνωστο προϊόν", "Unknown product")
            .Describe("Το προϊόν δεν υπάρχει για τη νομική οντότητα και τη δικαιοδοσία.", "The product does not exist for the legal entity and jurisdiction."),
        ErrorDefinition.For(ModuleCode.PFC, "NO-VERSION", 422, "Δεν υπάρχει ισχύουσα έκδοση", "No version in force")
            .Describe("Καμία κλειδωμένη έκδοση δεν ισχύει για το κανάλι και την ημερομηνία.", "No Locked version is in force for the channel and date."),
        ErrorDefinition.For(ModuleCode.PFC, "ABSTRACT", 422, "Αφηρημένη έκδοση", "Abstract version")
            .Describe("Μια αφηρημένη βάση δεν χρησιμοποιείται σε συναλλαγές.", "An abstract base cannot be used in transactions."),
        ErrorDefinition.For(ModuleCode.PFC, "NO-RATING", 422, "Λείπει η αναφορά τιμολόγησης", "No rating slot")
            .Describe("Η έκδοση δεν δηλώνει θέση τιμολόγησης.", "The version declares no rating slot."),
        ErrorDefinition.For(ModuleCode.PFC, "UNKNOWN-HASH", 404, "Άγνωστο αποτύπωμα", "Unknown artefact hash")
            .Describe("Δεν υπάρχει αντικείμενο προϊόντος με αυτό το αποτύπωμα για τη νομική σας οντότητα.", "No product artefact with this hash exists for your legal entity."),
        ErrorDefinition.For(ModuleCode.PFC, "UNKNOWN-ITEM", 422, "Άγνωστο στοιχείο", "Unknown item")
            .Describe("Το στοιχείο, η ερώτηση ή η απάντηση δεν υπάρχει στο προϊόν.", "The item, question or answer is not in the product."),
        ErrorDefinition.For(ModuleCode.PFC, "INVALID-DEFINITION", 422, "Ο ορισμός προϊόντος δεν είναι έγκυρος", "The product definition is not valid")
            .Describe("Ο ορισμός παραβιάζει κανόνες ελέγχου (PFC-LINT-*). Δείτε τα πεδία σφάλματος.", "The definition breaks lint rules (PFC-LINT-*). See the field errors."),
        ErrorDefinition.For(ModuleCode.PFC, "VERSION-EXISTS", 409, "Η έκδοση υπάρχει ήδη", "The version already exists")
            .Describe("Οι κλειδωμένες εκδόσεις είναι αμετάβλητες. Δημοσιεύστε νέα έκδοση.", "Locked versions are immutable. Publish a new version."),
        ErrorDefinition.For(ModuleCode.PFC, "WINDOW-OVERLAP", 409, "Επικάλυψη παραθύρων", "Windows overlap")
            .Describe("Άλλη κλειδωμένη έκδοση καλύπτει μέρος του παραθύρου νέων εργασιών.", "Another Locked version covers part of the new-business window."),
        ErrorDefinition.For(ModuleCode.PFC, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
