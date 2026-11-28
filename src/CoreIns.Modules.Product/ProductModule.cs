using CoreIns.Modules.Product.Authority;
using CoreIns.Modules.Product.Commands;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Persistence;
using CoreIns.Modules.Product.Queries;
using CoreIns.Modules.Product.Services;
using CoreIns.Platform;
using CoreIns.Platform.Approvals;
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
        services.AddScoped<ProductListQueries>();
        services.AddScoped<RuntimeQueries>();
        services.AddScoped<ProductSourceQueries>();

        // The one state-changing path: import a product source document (the seed and, later, the authoring workflow).
        services.AddScoped<IValidator<ImportProductVersion>, ImportProductVersionValidator>();
        services.AddCommandAuditor<ImportProductVersion, ProductVersionImportResponse, ImportProductVersionAuditor>();
        services.AddCommand<ImportProductVersion, ProductVersionImportResponse, ImportProductVersionHandler>(CommandDescriptor.For("pfc.ProductVersion.import"));

        // BO-PFC-03: YAML or JSON source into a draft change set (never a Locked version).
        services.AddScoped<IValidator<ImportProductSource>, ImportProductSourceValidator>();
        services.AddCommandAuditor<ImportProductSource, ProductImportImportResponse, ImportProductSourceAuditor>();
        services.AddCommand<ImportProductSource, ProductImportImportResponse, ImportProductSourceHandler>(CommandDescriptor.For("pfc.ProductImport.import") with { SupportsDryRun = true });

        // REQ-PFC-213 / D-SL5-09: emergency fall-back with maker-checker (PLT approval PFC.Fallback, authority PFC.EMERGENCY_CHANGE).
        services.AddProductAuthorityTypes();
        services.AddOwnerDecidedApprovalType(ProductAuthorityTypes.FallbackApprovalType);
        services.AddScoped<IValidator<RequestFallback>, RequestFallbackValidator>();
        services.AddCommandAuditor<RequestFallback, ProductVersionEmergencyFallbackResponse, RequestFallbackAuditor>();
        services.AddCommand<RequestFallback, ProductVersionEmergencyFallbackResponse, RequestFallbackHandler>(CommandDescriptor.For("pfc.ProductVersion.fallback") with { SupportsDryRun = true });
        services.AddScoped<IValidator<DecideFallback>, DecideFallbackValidator>();
        services.AddCommandAuditor<DecideFallback, ProductVersionDecideFallbackResponse, DecideFallbackAuditor>();
        services.AddCommand<DecideFallback, ProductVersionDecideFallbackResponse, DecideFallbackHandler>(CommandDescriptor.For("pfc.ProductVersion.decideFallback"));

        // BO-PFC-01: draft and change-set store (create from a Locked version, edit by JSON pointer with lint, abandon).
        services.AddScoped<IValidator<CreateChangeSet>, CreateChangeSetValidator>();
        services.AddCommandAuditor<CreateChangeSet, ChangeSetCreateResponse, CreateChangeSetAuditor>();
        services.AddCommand<CreateChangeSet, ChangeSetCreateResponse, CreateChangeSetHandler>(CommandDescriptor.For("pfc.ChangeSet.create"));
        services.AddScoped<IValidator<EditChangeSet>, EditChangeSetValidator>();
        services.AddCommandAuditor<EditChangeSet, ChangeSetEditResponse, EditChangeSetAuditor>();
        services.AddCommand<EditChangeSet, ChangeSetEditResponse, EditChangeSetHandler>(CommandDescriptor.For("pfc.ChangeSet.edit"));
        services.AddScoped<IValidator<AbandonChangeSet>, AbandonChangeSetValidator>();
        services.AddCommandAuditor<AbandonChangeSet, ChangeSetAbandonResponse, AbandonChangeSetAuditor>();
        services.AddCommand<AbandonChangeSet, ChangeSetAbandonResponse, AbandonChangeSetHandler>(CommandDescriptor.For("pfc.ChangeSet.abandon"));

        // BO-PFC-02: revert one logged edit; rebase the draft onto a newer Locked version (three-way merge).
        services.AddScoped<IValidator<RevertChangeSet>, RevertChangeSetValidator>();
        services.AddCommandAuditor<RevertChangeSet, ChangeSetRevertResponse, RevertChangeSetAuditor>();
        services.AddCommand<RevertChangeSet, ChangeSetRevertResponse, RevertChangeSetHandler>(CommandDescriptor.For("pfc.ChangeSet.revert"));
        services.AddScoped<IValidator<RebaseChangeSet>, RebaseChangeSetValidator>();
        services.AddCommandAuditor<RebaseChangeSet, ChangeSetRebaseResponse, RebaseChangeSetAuditor>();
        services.AddCommand<RebaseChangeSet, ChangeSetRebaseResponse, RebaseChangeSetHandler>(CommandDescriptor.For("pfc.ChangeSet.rebase"));

        // BO-PFC-04: release lifecycle (submit, sign-off with PLT approval PFC.Release / authority PFC.RELEASE, schedule, cancel) and the due-date publisher.
        services.AddOwnerDecidedApprovalType(ProductAuthorityTypes.ReleaseApprovalType);
        services.AddScoped<ReleasePublisher>();
        services.AddScoped<IValidator<SubmitChangeSet>, SubmitChangeSetValidator>();
        services.AddCommandAuditor<SubmitChangeSet, ProductVersionSubmitResponse, SubmitChangeSetAuditor>();
        services.AddCommand<SubmitChangeSet, ProductVersionSubmitResponse, SubmitChangeSetHandler>(CommandDescriptor.For("pfc.ProductVersion.submit") with { SupportsDryRun = true });
        services.AddScoped<IValidator<SignOffRelease>, SignOffReleaseValidator>();
        services.AddCommandAuditor<SignOffRelease, ProductVersionSignOffResponse, SignOffReleaseAuditor>();
        services.AddCommand<SignOffRelease, ProductVersionSignOffResponse, SignOffReleaseHandler>(CommandDescriptor.For("pfc.ProductVersion.signOff"));
        services.AddScoped<IValidator<ScheduleRelease>, ScheduleReleaseValidator>();
        services.AddCommandAuditor<ScheduleRelease, ProductVersionScheduleResponse, ScheduleReleaseAuditor>();
        services.AddCommand<ScheduleRelease, ProductVersionScheduleResponse, ScheduleReleaseHandler>(CommandDescriptor.For("pfc.ProductVersion.schedule") with { SupportsDryRun = true });
        services.AddScoped<IValidator<CancelScheduledRelease>, CancelScheduledReleaseValidator>();
        services.AddCommandAuditor<CancelScheduledRelease, ProductVersionCancelScheduleResponse, CancelScheduledReleaseAuditor>();
        services.AddCommand<CancelScheduledRelease, ProductVersionCancelScheduleResponse, CancelScheduledReleaseHandler>(CommandDescriptor.For("pfc.ProductVersion.cancelSchedule"));
        services.AddCommand<PublishDueRelease, string, PublishDueReleaseHandler>(CommandDescriptor.For("pfc.ProductVersion.publishDue") with { RequiresIdempotencyKey = false, Idempotent = false });
        services.AddSingleton<ReleaseSweepRunner>();
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<ReleaseSweeper>();
        }

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<IProductProductVersionService, ProductProductVersionService>();
        services.AddScoped<IProductCatalogueService, ProductCatalogueService>();
        services.AddScoped<IProductChargeTypeService, ProductChargeTypeService>();
        services.AddScoped<IProductQuestionSetService, ProductQuestionSetService>();
        services.AddScoped<IProductArtifactService, ProductArtifactService>();
        services.AddScoped<IProductAvailabilityService, ProductAvailabilityService>();
        services.AddScoped<IProductProductService, ProductProductService>();
        // IProductPolicyDraftService (ProductPolicyDraftService) is deliberately not registered yet: POL still sends its own RiskTree, which must be
        // mapped to the PolicyDraft shape first (TODO(hardening), BO-POL); until then POL keeps its documented fail-closed/missing-service path.

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
        ErrorDefinition.For(ModuleCode.PFC, "FALLBACK-SOURCE", 422, "Δεν υπάρχει έγκυρη έκδοση πηγής", "No valid fall-back source")
            .Describe("Δεν υπάρχει προηγούμενη δημοσιευμένη έκδοση με το ίδιο εύρος καναλιών για αντιγραφή.", "There is no earlier published version with the same channel scope to copy."),
        ErrorDefinition.For(ModuleCode.PFC, "FALLBACK-STATE", 409, "Η επαναφορά δεν είναι σε κατάλληλη κατάσταση", "The fall-back is not in a valid state")
            .Describe("Η έκδοση έχει ήδη αντικατασταθεί ή εκκρεμεί επαναφορά, ή η επαναφορά δεν είναι σε αναμονή έγκρισης.", "The version is already replaced or has a fall-back pending, or the fall-back is not awaiting approval."),
        ErrorDefinition.For(ModuleCode.PFC, "FALLBACK-NOT-FOUND", 404, "Άγνωστη επαναφορά", "Unknown fall-back")
            .Describe("Η αίτηση επαναφοράς δεν υπάρχει.", "The fall-back request does not exist."),
        ErrorDefinition.For(ModuleCode.PFC, "SOD", 403, "Παραβίαση διαχωρισμού καθηκόντων", "Separation of duties")
            .Describe("Η έγκριση δεν δεσμεύει το περιεχόμενο αυτής της επαναφοράς ή δεν δόθηκε με την απαιτούμενη εξουσιοδότηση.", "The approval does not bind this fall-back's content or was not decided under the required authority."),
        ErrorDefinition.For(ModuleCode.PFC, "STALE", 409, "Το προϊόν άλλαξε", "The product changed")
            .Describe("Το προϊόν τροποποιήθηκε ταυτόχρονα. Φορτώστε ξανά και δοκιμάστε πάλι.", "The product was changed concurrently. Reload and retry."),
        ErrorDefinition.For(ModuleCode.PFC, "CHANGE-SET-NOT-FOUND", 404, "Άγνωστο σύνολο αλλαγών", "Unknown change set")
            .Describe("Το σύνολο αλλαγών δεν υπάρχει για τη νομική σας οντότητα.", "The change set does not exist for your legal entity."),
        ErrorDefinition.For(ModuleCode.PFC, "CHANGE-SET-STATE", 409, "Το σύνολο αλλαγών δεν είναι ανοιχτό", "The change set is not open")
            .Describe("Μόνο ανοιχτό σύνολο αλλαγών μπορεί να τροποποιηθεί ή να εγκαταλειφθεί.", "Only an open change set can be edited or abandoned."),
        ErrorDefinition.For(ModuleCode.PFC, "INVALID-EDIT", 422, "Μη έγκυρη επεξεργασία", "Invalid edit")
            .Describe("Η διαδρομή ή η τιμή της επεξεργασίας δεν ταιριάζει με το προσχέδιο.", "The edit's path or value does not fit the draft."),
        ErrorDefinition.For(ModuleCode.PFC, "IMPORT-INVALID", 422, "Μη έγκυρη πηγή εισαγωγής", "Invalid import source")
            .Describe("Η πηγή YAML ή JSON δεν είναι έγκυρος ορισμός προϊόντος.", "The YAML or JSON source is not a valid product definition."),
        ErrorDefinition.For(ModuleCode.PFC, "INVALID-REQUEST", 400, "Μη έγκυρο αίτημα", "Invalid request")
            .Describe("Μια παράμετρος του αιτήματος λείπει ή δεν είναι έγκυρη.", "A request parameter is missing or not valid."),
        ErrorDefinition.For(ModuleCode.PFC, "CONFLICT", 409, "Σύγκρουση αλλαγών", "Change conflict")
            .Describe("Η ίδια διαδρομή άλλαξε και στο προσχέδιο και στη νεότερη έκδοση, ή μια μεταγενέστερη αλλαγή την επηρεάζει.", "The same path changed on both sides, or a later change touches it."),
        ErrorDefinition.For(ModuleCode.PFC, "CHECKS-FAILED", 422, "Οι έλεγχοι υποβολής απέτυχαν", "Submit checks failed")
            .Describe("Το προσχέδιο έχει ευρήματα ελέγχου ή αλλάζει την ταυτότητα του προϊόντος. Δείτε τα πεδία σφάλματος.", "The draft has lint findings or changes the product identity. See the field errors."),
        ErrorDefinition.For(ModuleCode.PFC, "RELEASE-STATE", 409, "Η κυκλοφορία δεν είναι σε κατάλληλη κατάσταση", "The release is not in a valid state")
            .Describe("Η ενέργεια δεν επιτρέπεται στην τρέχουσα κατάσταση της κυκλοφορίας (υποβολή, έγκριση, προγραμματισμός).", "The action is not allowed in the release's current state (submitted, approved, scheduled)."),
        ErrorDefinition.For(ModuleCode.PFC, "INVALID-WINDOW", 422, "Μη έγκυρο παράθυρο", "Invalid window")
            .Describe("Η ημερομηνία ενεργοποίησης ή τα παράθυρα δεν είναι έγκυρα.", "The activation date or the windows are not valid."),
        ErrorDefinition.For(ModuleCode.PFC, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
