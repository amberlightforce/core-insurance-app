using CoreIns.Modules.Documents.Services.Dsar;
using CoreIns.SharedKernel.Dsar;
using CoreIns.Modules.Documents.Commands;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Persistence;
using CoreIns.Modules.Documents.Queries;
using CoreIns.Modules.Documents.Services;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Documents;

/// <summary>
/// Composition entry point of the Documents module (PRD-10 Documents). SL6-DOC-CORE builds a minimal service: letters
/// from four HTML templates (el, en), stub delivery channels, delivery evidence and the statutory delivery proof.
/// </summary>
public static class DocumentsModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "doc";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: SELECT, INSERT, UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<DocumentsDbContext>(ModuleCode.DOC, Schema)];

    /// <summary>Registers the module's services: DbContext, commands, reader, the request SPI and errors.</summary>
    public static IServiceCollection AddDocumentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DocumentsOptions>(configuration.GetSection(DocumentsOptions.Section));
        services.AddModuleDbContext<DocumentsDbContext>(Schema);
        services.AddSingleton<IDsarParticipant, DocumentsDsarParticipant>();
        services.AddScoped<DocumentReader>();

        // The request is idempotent on (template key, request number) by itself; callers inside a unit of work pass no HTTP key.
        services.AddScoped<IValidator<RequestDocument>, RequestDocumentValidator>();
        services.AddCommand<RequestDocument, DocumentRequestOutcome, RequestDocumentHandler>(
            CommandDescriptor.For("doc.Document.request") with { RequiresIdempotencyKey = false });
        services.AddScoped<IValidator<ReceiveProviderEvent>, ReceiveProviderEventValidator>();
        services.AddCommand<ReceiveProviderEvent, ProviderEventResult, ReceiveProviderEventHandler>(
            CommandDescriptor.For("doc.ProviderEvent.receive") with { RequiresIdempotencyKey = false });
        services.AddCommand<ReprintDocument, ReprintResult, ReprintDocumentHandler>(CommandDescriptor.For("doc.Document.reprint"));
        services.AddCommand<StoreArchive, ArchiveStoreResponse, StoreArchiveHandler>(CommandDescriptor.For("doc.Archive.store"));

        services.AddScoped<IDocumentRequestService, DocumentRequestService>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every DOC-ERR code this slice raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.DOC, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει στη νομική σας οντότητα.", "The record does not exist in your legal entity."),
        ErrorDefinition.For(ModuleCode.DOC, "PAYLOAD-INVALID", 422, "Μη έγκυρα δεδομένα εγγράφου", "The document data is not valid")
            .Describe("Το πρότυπο ή οι τιμές του δεν είναι έγκυρα.", "The template or its placeholder values are not valid."),
        ErrorDefinition.For(ModuleCode.DOC, "LANGUAGE-NOT-APPROVED", 422, "Η γλώσσα δεν είναι εγκεκριμένη", "The language is not approved")
            .Describe("Επιτρέπονται μόνο τα ελληνικά και τα αγγλικά.", "Only Greek and English are approved."),
        ErrorDefinition.For(ModuleCode.DOC, "CHANNEL-NOT-PERMITTED", 403, "Το κανάλι δεν επιτρέπεται", "The channel is not permitted")
            .Describe("Το μέσο ή ο πάροχος δεν είναι διαθέσιμος για αυτή την παράδοση.", "The medium or provider is not available for this delivery."),
        ErrorDefinition.For(ModuleCode.DOC, "HASH-MISMATCH", 422, "Το αποτύπωμα δεν ταιριάζει", "The hash does not match")
            .Describe("Το δηλωμένο αποτύπωμα SHA-256 διαφέρει από το περιεχόμενο.", "The declared SHA-256 differs from the content."),
    ];
}
