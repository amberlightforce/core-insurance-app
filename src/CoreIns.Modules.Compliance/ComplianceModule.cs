using CoreIns.Modules.Compliance.Clocks;
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

        // Clock engine (SL6-CMP-CLOCK): commands, queries, in-process contracts and the sweeper (worker role only).
        services.AddOptions<ClockOptions>().Bind(configuration.GetSection(ClockOptions.Section));
        services.AddScoped<ClockEngine>();
        services.AddScoped<ClockReader>();
        services.AddScoped<IValidator<StartClock>, StartClockValidator>();
        services.AddScoped<IValidator<PauseClock>, PauseClockValidator>();
        services.AddScoped<IValidator<ResumeClock>, ResumeClockValidator>();
        services.AddScoped<IValidator<StopClock>, StopClockValidator>();
        services.AddScoped<IValidator<CancelClock>, CancelClockValidator>();
        services.AddScoped<IValidator<ExtendClock>, ExtendClockValidator>();
        services.AddCommand<StartClock, ClockStartResponse, StartClockHandler>(CommandDescriptor.For("cmp.Clock.start") with { SupportsDryRun = true });
        services.AddCommand<PauseClock, ClockPauseResponse, PauseClockHandler>(CommandDescriptor.For("cmp.Clock.pause"));
        services.AddCommand<ResumeClock, ClockResumeResponse, ResumeClockHandler>(CommandDescriptor.For("cmp.Clock.resume"));
        services.AddCommand<StopClock, ClockStopResponse, StopClockHandler>(CommandDescriptor.For("cmp.Clock.stop"));
        services.AddCommand<CancelClock, ClockCancelResponse, CancelClockHandler>(CommandDescriptor.For("cmp.Clock.cancel"));
        services.AddCommand<ExtendClock, ClockExtendResponse, ExtendClockHandler>(CommandDescriptor.For("cmp.Clock.extend"));
        services.AddCommand<SweepClock, string, SweepClockHandler>(CommandDescriptor.For("cmp.Clock.sweep") with { RequiresIdempotencyKey = false, Idempotent = false });
        services.AddScoped<IComplianceClockService, ComplianceClockService>();
        services.AddScoped<IComplianceClockDefService, ComplianceClockDefService>();
        services.AddSingleton<ClockSweepRunner>();
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<ClockSweeper>();
        }

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
        ErrorDefinition.For(ModuleCode.CMP, "VALIDATION", 422, "Μη έγκυρο αίτημα παραστατικού", "The fiscal-document request is not valid")
            .Describe("Η συσχέτιση ισχύει μόνο για πιστωτικά και πρέπει να συμφωνεί με το αρχικό παραστατικό.", "A correlation applies to credits only and must match the original document."),
        ErrorDefinition.For(ModuleCode.CMP, "CORRELATED-NOT-FOUND", 422, "Δεν βρέθηκε το συσχετισμένο παραστατικό", "The correlated document was not found")
            .Describe("Το πιστωτικό πρέπει να αναφέρεται σε καταχωρημένο παραστατικό έκδοσης του ίδιου αντισυμβαλλομένου (όχι σε άλλο πιστωτικό).", "A credit must refer to a Registered ISSUE document of the same counterparty (not to another credit)."),
        ErrorDefinition.For(ModuleCode.CMP, "IDEMPOTENCY-MISMATCH", 409, "Το κλειδί παραστατικού χρησιμοποιήθηκε με διαφορετικό περιεχόμενο", "The fiscal key was already used with different content")
            .Describe("Η ίδια πηγή, ρόλος και αναθεώρηση υπάρχουν ήδη με άλλο αντισυμβαλλόμενο ή ποσά.", "The same source, role and revision already exist with another counterparty or amounts."),
        ErrorDefinition.For(ModuleCode.CMP, "CLOCK-UNKNOWN", 422, "Άγνωστο ρολόι", "Unknown clock")
            .Describe("Δεν υπάρχει ορισμός ρολογιού με αυτόν τον κωδικό.", "No clock definition has this code."),
        ErrorDefinition.For(ModuleCode.CMP, "CLOCK-NO-VALUE", 422, "Το ρολόι δεν έχει τιμή για τη δικαιοδοσία", "The clock has no value for the jurisdiction")
            .Describe("Ο ορισμός δεν ισχύει για τη δικαιοδοσία της νομικής οντότητας.", "The definition does not apply to the legal entity's jurisdiction."),
        ErrorDefinition.For(ModuleCode.CMP, "CLOCK-STATE", 422, "Η ενέργεια δεν επιτρέπεται στην κατάσταση του ρολογιού", "The action is not allowed in the clock's state")
            .Describe("Η τρέχουσα κατάσταση του ρολογιού δεν επιτρέπει αυτή την ενέργεια.", "The clock's current state does not allow this action."),
        ErrorDefinition.For(ModuleCode.CMP, "CLOCK-CANCEL-REASON", 422, "Απαιτείται αιτιολογία ακύρωσης", "A cancellation reason is required")
            .Describe("Η ακύρωση ρολογιού χρειάζεται αιτιολογία.", "Cancelling a clock needs a reason."),
        ErrorDefinition.For(ModuleCode.CMP, "EXTENSION-NOT-ALLOWED", 422, "Το ρολόι δεν επιδέχεται παράταση", "The clock cannot be extended")
            .Describe("Ο ορισμός του ρολογιού δεν προβλέπει παράταση.", "The clock definition does not allow an extension."),
        ErrorDefinition.For(ModuleCode.CMP, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
