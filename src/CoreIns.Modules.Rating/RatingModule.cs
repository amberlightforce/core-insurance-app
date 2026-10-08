using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Modules.Rating.Persistence;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Rating;

/// <summary>
/// Composition entry point of the Rating module (PRD-03). SL-RAT-UW slice: the rating runtime for one motor product on
/// illustrative decision tables (D-SLC-04), tax and levy rates from MKT configuration, the explainable worksheet.
/// </summary>
public static class RatingModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "rat";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: EF Core migrations of <c>rat</c>, then SELECT/INSERT/UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<RatingDbContext>(ModuleCode.RAT, Schema)];

    /// <summary>Registers the module's services: DbContext, store, in-process contracts, error definitions.</summary>
    public static IServiceCollection AddRatingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RatingOptions>().Bind(configuration.GetSection(RatingOptions.Section));
        services.AddModuleDbContext<RatingDbContext>(Schema);
        services.AddScoped<RatingStore>();

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<IRatingRateService, RatingRateService>();
        services.AddScoped<IRatingRatingArtifactService, RatingRatingArtifactService>();
        services.AddScoped<IRatingWorksheetService, RatingWorksheetService>();
        services.AddScoped<IRatingProrationEngine, RatingProrationEngine>(); // SL3-RAT-PRORATE
        services.AddScoped<IRatingServicingTax, RatingServicingTax>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every RAT-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.RAT, "ENVELOPE", 422, "Ο φάκελος του αιτήματος τιμολόγησης δεν είναι έγκυρος", "The rating envelope is not valid")
            .Describe("Ελέγξτε νομική οντότητα, τρόπο λειτουργίας και τμήματα.", "Check the legal entity, the mode and the segments."),
        ErrorDefinition.For(ModuleCode.RAT, "INPUT", 422, "Τα στοιχεία κινδύνου δεν είναι έγκυρα", "The risk data is not valid")
            .Describe("Ένα πεδίο του δέντρου κινδύνου λείπει ή έχει λάθος μορφή· το μήνυμα το κατονομάζει.", "A field of the risk tree is missing or malformed; the message names it."),
        ErrorDefinition.For(ModuleCode.RAT, "INPUT-UNDECLARED", 422, "Άγνωστο πεδίο στα στοιχεία κινδύνου", "A risk field is not declared")
            .Describe("Το σχήμα εισόδου του πακέτου τιμολόγησης δεν δηλώνει αυτό το πεδίο.", "The rating artefact's input schema does not declare this field."),
        ErrorDefinition.For(ModuleCode.RAT, "PERIOD", 422, "Η περίοδος του τμήματος δεν υποστηρίζεται", "The segment period is not supported")
            .Describe("Η τρέχουσα έκδοση τιμολογεί μόνο ετήσιες διάρκειες.", "This release rates annual terms only."),
        ErrorDefinition.For(ModuleCode.RAT, "CURRENCY", 422, "Το νόμισμα δεν ταιριάζει με το πακέτο τιμολόγησης", "The currency does not match the rating artefact")
            .Describe("Το πακέτο τιμολογεί σε άλλο νόμισμα.", "The artefact rates in a different currency."),
        ErrorDefinition.For(ModuleCode.RAT, "NO-ACTIVE-ARTEFACT", 422, "Δεν υπάρχει ενεργό πακέτο τιμολόγησης", "No rating artefact is active")
            .Describe("Δεν υπάρχει ενεργοποίηση για το προϊόν και την ημερομηνία.", "There is no activation for the product and date."),
        ErrorDefinition.For(ModuleCode.RAT, "UNKNOWN-ARTEFACT", 422, "Άγνωστο πακέτο τιμολόγησης", "Unknown rating artefact")
            .Describe("Το πακέτο που κατονομάστηκε δεν υπάρχει.", "The named artefact does not exist."),
        ErrorDefinition.For(ModuleCode.RAT, "INCOMPATIBLE-ARTEFACT", 422, "Το πακέτο τιμολόγησης δεν ταιριάζει με την έκδοση προϊόντος", "The rating artefact does not match the product version")
            .Describe("Το πακέτο δεσμεύεται σε άλλο προϊόν ή άλλη έκδοση.", "The artefact is bound to another product or version."),
        ErrorDefinition.For(ModuleCode.RAT, "DOMAIN", 422, "Η τιμολόγηση δεν μπορεί να ολοκληρωθεί", "Rating cannot be completed")
            .Describe("Ένας πίνακας ή μια έκφραση δεν έδωσε αποτέλεσμα για αυτό το ρίσκο.", "A table or expression gave no result for this risk."),
        ErrorDefinition.For(ModuleCode.RAT, "HIT-POLICY", 422, "Παραβίαση πολιτικής επιλογής γραμμής πίνακα", "Table hit policy violated")
            .Describe("Περισσότερες από μία γραμμές ταίριαξαν σε πίνακα μοναδικής επιλογής.", "More than one row matched a unique-hit table."),
        ErrorDefinition.For(ModuleCode.RAT, "SCALE", 422, "Απώλεια ακρίβειας σε υπολογισμό ποσού", "Precision would be lost in an amount calculation")
            .Describe("Ο υπολογισμός δεν αναπαρίσταται ακριβώς· δηλώστε ρητή στρογγυλοποίηση.", "The calculation cannot be represented exactly; declare explicit rounding."),
        ErrorDefinition.For(ModuleCode.RAT, "CONVENTION", 422, "Η σύμβαση μέτρησης ημερών δεν υποστηρίζεται από το προϊόν", "The day-count convention is not declared by the product")
            .Describe("Το προϊόν δεν δηλώνει τη σύμβαση ή δεν έχει υλοποιηθεί· η αναλογική κατανομή σταματά.", "The product does not declare the convention, or it is not built; proration stops."),
        ErrorDefinition.For(ModuleCode.RAT, "TAX", 422, "Οι φόροι και οι εισφορές δεν μπορούν να υπολογιστούν", "Taxes and levies cannot be calculated")
            .Describe("Η παραμετροποίηση της αγοράς δεν έδωσε συντελεστή· η τιμολόγηση σταματά.", "Market configuration gave no rate; rating stops (fail closed)."),
        ErrorDefinition.For(ModuleCode.RAT, "DATA-UNAVAILABLE", 503, "Η λειτουργία δεν είναι διαθέσιμη", "The operation is not available")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών ή μια εξάρτηση δεν απάντησε.", "The operation belongs to a later work package or a dependency did not answer."),
        ErrorDefinition.For(ModuleCode.RAT, "WORKSHEET-NOT-FOUND", 404, "Δεν βρέθηκε το φύλλο τιμολόγησης", "Worksheet not found")
            .Describe("Το φύλλο δεν υπάρχει στη νομική σας οντότητα.", "The worksheet does not exist in your legal entity."),
    ];
}
