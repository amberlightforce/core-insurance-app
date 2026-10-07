using CoreIns.Modules.Compliance.Commands;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Modules.Compliance.Queries;
using CoreIns.Modules.Compliance.Services;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Compliance;

/// <summary>
/// Composition entry point of the Compliance module (PRD-11 Compliance). The Host calls <see cref="AddComplianceModule"/>;
/// the migrate job applies <see cref="Databases"/>. SL-BIL builds only the fiscal-document request path on the
/// <c>FiscalDocumentChannel</c> SPI (W5-CMP-01 subset, a stub channel bound by the Host outside Production).
/// </summary>
public static class ComplianceModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "cmp";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>The module database for the migrate job: SELECT, INSERT, UPDATE for the app role (no DELETE).</summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
        [ModuleDbContextRegistration.Define<ComplianceDbContext>(ModuleCode.CMP, Schema)];

    /// <summary>Registers the module's services: DbContext, the fiscal-document command and query, in-process contract, errors.</summary>
    public static IServiceCollection AddComplianceModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<ComplianceDbContext>(Schema);
        services.AddScoped<FiscalDocumentReader>();

        services.AddScoped<IValidator<RequestFiscalDocument>, RequestFiscalDocumentValidator>();
        services.AddCommandAuditor<RequestFiscalDocument, FiscalDocumentRequestResponse, RequestFiscalDocumentAuditor>();
        services.AddCommand<RequestFiscalDocument, FiscalDocumentRequestResponse, RequestFiscalDocumentHandler>(
            CommandDescriptor.For("cmp.FiscalDocument.request") with { SupportsDryRun = true });

        services.AddScoped<IComplianceFiscalDocumentService, ComplianceFiscalDocumentService>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every CMP-ERR code the fiscal path raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.CMP, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει στη νομική σας οντότητα.", "The record does not exist in your legal entity."),
        ErrorDefinition.For(ModuleCode.CMP, "SOURCE-UNKNOWN", 422, "Άγνωστη πηγή φορολογικού παραστατικού", "Unknown fiscal source")
            .Describe("Ο τύπος πηγής δεν είναι αποδεκτός για φορολογικό παραστατικό.", "The source type is not accepted for a fiscal document."),
        ErrorDefinition.For(ModuleCode.CMP, "FISCAL-TOTAL", 422, "Τα ποσά του παραστατικού δεν είναι έγκυρα", "The document amounts are not valid")
            .Describe("Οι γραμμές πρέπει να έχουν ένα νόμισμα και στρογγυλοποίηση σε λεπτά.", "Lines must share one currency and be rounded to minor units."),
        ErrorDefinition.For(ModuleCode.CMP, "TRANSPORT-UNAVAILABLE", 503, "Το κανάλι φορολογικών παραστατικών δεν είναι διαθέσιμο", "The fiscal-document channel is not available", retryable: true)
            .Describe("Δεν έχει συνδεθεί κανάλι διαβίβασης για τη νομική οντότητα.", "No transmission channel is bound for the legal entity."),
        ErrorDefinition.For(ModuleCode.CMP, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
