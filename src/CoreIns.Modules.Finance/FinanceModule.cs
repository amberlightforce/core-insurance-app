using System.Text.Json;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Modules.Finance.Queries;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Finance;

/// <summary>
/// Composition entry point of the Finance module (PRD-09 Finance sub-ledger). The Host calls <see cref="AddFinanceModule"/>;
/// the migrate job applies <see cref="Databases"/>. SL-FIN slice: event intake in the worker (BIL BillingEntryPosted is
/// the posting source, POL and other BIL events are context, D-SLC-12), data-driven posting rules, balanced append-only
/// journals, a minimal GR-TEST chart, and the journal and posting-rule read APIs. SL2-FIN-CLM adds the claims postings
/// (D-SL2-08): CLM ReserveChanged and PaymentIssued plus BIL's disbursement entries, claim dimensions on journal lines,
/// and journals listed by claim.
/// </summary>
public static class FinanceModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "fin";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>
    /// The module database for the migrate job: EF Core migrations of <c>fin</c> (tables, triggers, seed), then the app
    /// role's privileges — reference data read-only, journals insert-only (REQ-FIN-070), business events updatable.
    /// </summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
    [
        new(ModuleCode.FIN, Schema, connectionString => ModuleDbContextRegistration.CreateForMigration<FinanceDbContext>(connectionString, Schema), FinanceSql.Grants),
    ];

    /// <summary>Registers the module's services: DbContext, intake handlers, posting, queries and error definitions.</summary>
    public static IServiceCollection AddFinanceModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<FinanceDbContext>(Schema);
        services.AddScoped<ReferenceData>();
        services.AddScoped<JournalWriter>();
        services.AddScoped<JournalReversal>();
        services.AddScoped<TaxTreatmentCheck>();
        services.AddScoped<Intake>();
        services.AddScoped<JournalReader>();
        services.AddScoped<PostingRuleReader>();

        // Intake (worker): the posting source, the policy context and the context-only events (REQ-FIN-001, -036).
        services.AddEventHandler<JsonElement, BillingEntryPostedHandler>(Descriptor(BillingEntryPostedV1.Descriptor), "FIN.Intake.BillingEntryPosted", ModuleCode.FIN);
        services.AddEventHandler<PolicyBoundContext, PolicyBoundHandler>(Descriptor(PolicyBoundV1.Descriptor), "FIN.Intake.PolicyBound", ModuleCode.FIN);
        services.AddScoped<PolicyBoundHandler>();
        services.AddEventHandler<RenewalBoundContext, RenewalBoundHandler>(Descriptor(RenewalBoundV1.Descriptor), "FIN.Intake.RenewalBound", ModuleCode.FIN);
        services.AddEventHandler<JsonElement, ContextEventHandler>(Descriptor(ChargeDeltaEmittedV1.Descriptor), "FIN.Intake.ChargeDeltaEmitted", ModuleCode.FIN);
        services.AddEventHandler<JsonElement, ContextEventHandler>(Descriptor(InvoiceIssuedV1.Descriptor), "FIN.Intake.InvoiceIssued", ModuleCode.FIN);
        services.AddEventHandler<JsonElement, ContextEventHandler>(Descriptor(PaymentReceivedV1.Descriptor), "FIN.Intake.PaymentReceived", ModuleCode.FIN);
        services.AddEventHandler<JsonElement, ContextEventHandler>(Descriptor(CashAllocatedV1.Descriptor), "FIN.Intake.CashAllocated", ModuleCode.FIN);

        // Claims (D-SL2-08): CLM ReserveChanged and PaymentIssued post; the cash side comes from BIL's disbursement
        // entries (BillingEntryPosted above); BIL Disbursement* and the other CLM events are context only.
        services.AddEventHandler<JsonElement, ClaimFactHandler>(Descriptor(ReserveChangedV1.Descriptor), "FIN.Intake.ReserveChanged", ModuleCode.FIN);
        services.AddEventHandler<JsonElement, ClaimFactHandler>(Descriptor(PaymentIssuedV1.Descriptor), "FIN.Intake.PaymentIssued", ModuleCode.FIN);
        foreach (var (contract, name) in new (EventContract, string)[]
                 {
                     (ClaimReportedV1.Descriptor, "ClaimReported"),
                     (ExposureCreatedV1.Descriptor, "ExposureCreated"),
                     (TransactionSetApprovedV1.Descriptor, "TransactionSetApproved"),
                     (ClaimClosedV1.Descriptor, "ClaimClosed"),
                     (DisbursementIssuedV1.Descriptor, "DisbursementIssued"),
                     (DisbursementClearedV1.Descriptor, "DisbursementCleared"),
                     (DisbursementRejectedV1.Descriptor, "DisbursementRejected"),
                     (DisbursementStoppedV1.Descriptor, "DisbursementStopped"),
                     (DisbursementVoidedV1.Descriptor, "DisbursementVoided"),
                     (DisbursementReturnedV1.Descriptor, "DisbursementReturned"),
                 })
        {
            services.AddEventHandler<JsonElement, ContextEventHandler>(Descriptor(contract), "FIN.Intake." + name, ModuleCode.FIN);
        }

        services.AddOptions<FinanceOptions>().Bind(configuration.GetSection(FinanceOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    private static EventDescriptor Descriptor(EventContract contract) => EventDescriptor.From(contract);

    /// <summary>Status, bilingual title and description of every FIN-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.FIN, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει για τη νομική σας οντότητα.", "The record does not exist for your legal entity."),
        ErrorDefinition.For(ModuleCode.FIN, "VALIDATION", 400, "Μη έγκυρο αίτημα", "Invalid request")
            .Describe("Ελέγξτε τις παραμέτρους του αιτήματος.", "Check the request parameters."),
        ErrorDefinition.For(ModuleCode.FIN, "ALREADY-REVERSED", 409, "Η εγγραφή έχει ήδη αντιλογιστεί", "The journal is already reversed")
            .Describe("Μια ημερολογιακή εγγραφή αντιλογίζεται μία μόνο φορά.", "A journal can be reversed only once."),
        ErrorDefinition.For(ModuleCode.FIN, "UNBALANCED", 422, "Μη ισοσκελισμένη εγγραφή", "Unbalanced journal")
            .Describe("Οι χρεώσεις πρέπει να ισούνται με τις πιστώσεις σε κάθε νόμισμα.", "Debits must equal credits in every currency."),
    ];
}
