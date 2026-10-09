using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Commands.Cancellation;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Events;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Queries;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Underwriting.Contracts.Events;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.Modules.Policy;

/// <summary>
/// Composition entry point of the Policy module (PRD-05 Policy administration). The Host calls <see cref="AddPolicyModule"/>;
/// the migrate job applies <see cref="Databases"/>. SL-POL builds the quote → bind → policy slice: submission, draft risk
/// data, quote (RAT + UW), bind (policy, term, issuance transaction, segment, charge deltas) and the as-of reads.
/// PFC, RAT, UW, MKT rounding and PTY are called only through their generated in-process contracts (D-ARC-16).
/// </summary>
public static class PolicyModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "pol";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>
    /// The module database for the migrate job. Least privilege per table: working rows (job, quote version) and the
    /// policy rows that are end-dated (term, segment: only their record period closes, enforced by trigger) get SELECT, INSERT,
    /// UPDATE; the policy row, the append-only transaction log and charge deltas get SELECT, INSERT only (REQ-POL-075). Nothing gets DELETE.
    /// </summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
    [
        new(
            ModuleCode.POL,
            Schema,
            connectionString => ModuleDbContextRegistration.CreateForMigration<PolicyDbContext>(connectionString, Schema),
            appRole =>
            [
                $"REVOKE ALL ON ALL TABLES IN SCHEMA {Schema} FROM {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.job, {Schema}.quote_version, {Schema}.policy_term, {Schema}.segment TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.policy, {Schema}.policy_transaction, {Schema}.charge_line TO {appRole}",
                // The policy row is frozen (trigger); the one thing a command changes is the record-time watermark (D-SL3-03).
                $"GRANT UPDATE (last_recorded_at, record_version) ON {Schema}.policy TO {appRole}",
                $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {Schema} TO {appRole}",
            ]),
    ];

    /// <summary>Registers the module's services: DbContext, commands, queries, in-process contracts, event handlers, error definitions.</summary>
    public static IServiceCollection AddPolicyModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PolicyOptions>().Bind(configuration.GetSection(PolicyOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddModuleDbContext<PolicyDbContext>(Schema);

        services.AddScoped(typeof(Dependency<>));
        services.AddScoped<RiskTrees>();
        services.AddScoped<RatingInput>();
        services.AddScoped<JobReader>();
        services.AddScoped<PolicyReader>();
        services.AddScoped<PolicySnapshots>();
        services.AddScoped<PolicySearch>();

        services.AddScoped<IValidator<CreateSubmission>, CreateSubmissionValidator>();
        services.AddCommandAuditor<CreateSubmission, SubmissionCreateResponse, CreateSubmissionAuditor>();
        services.AddCommand<CreateSubmission, SubmissionCreateResponse, CreateSubmissionHandler>(
            CommandDescriptor.For("pol.Submission.create") with { SupportsDryRun = true });

        services.AddScoped<IValidator<UpdateDraft>, UpdateDraftValidator>();
        services.AddCommandAuditor<UpdateDraft, JobUpdateDraftResponse, UpdateDraftAuditor>();
        services.AddCommand<UpdateDraft, JobUpdateDraftResponse, UpdateDraftHandler>(CommandDescriptor.For("pol.Job.updateDraft") with { SupportsDryRun = true });

        services.AddScoped<IValidator<QuoteJob>, QuoteJobValidator>();
        services.AddCommandAuditor<QuoteJob, JobQuoteResponse, QuoteJobAuditor>();
        services.AddCommand<QuoteJob, JobQuoteResponse, QuoteJobHandler>(CommandDescriptor.For("pol.Job.quote") with { SupportsDryRun = true });

        services.AddScoped<IValidator<BindJob>, BindJobValidator>();
        services.AddCommandAuditor<BindJob, JobBindResponse, BindJobAuditor>();
        services.AddCommand<BindJob, JobBindResponse, BindJobHandler>(CommandDescriptor.For("pol.Job.bind") with { SupportsDryRun = true });

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<IPolicySubmissionService, PolicySubmissionService>();
        services.AddScoped<IPolicyJobService, PolicyJobService>();
        services.AddScoped<IPolicyPolicyService, PolicyPolicyService>();
        services.AddScoped<IPolicyTermService, PolicyTermService>();
        services.AddScoped<IPolicySnapshotService, PolicySnapshotService>();

        // UW decides declines; POL marks the job (REQ-POL-156).
        services.AddEventHandler<DeclineIssuedV1, DeclineIssuedHandler>(EventDescriptor.From(DeclineIssuedV1.Descriptor), DeclineIssuedHandler.Name, ModuleCode.POL);

        // SL3-POL-CANCEL: policyholder cancellation now / flat (pol.Cancellation.create; dry run = the refund preview).
        services.AddScoped<ICancellationRefundMethods, IllustrativeRefundMethods>();
        // The production IProration is RAT's shared proration behind POL's port (never ReferenceProration, which is test-only, PITFALLS 43).
        // Replace, so exactly one registration survives next to POL-CHANGE's default.
        services.AddScoped<RatingProrationAdapter>();
        services.Replace(ServiceDescriptor.Scoped<IProration>(sp => sp.GetRequiredService<RatingProrationAdapter>()));
        services.AddScoped<IValidator<CancelPolicy>, CancelPolicyValidator>();
        services.AddCommandAuditor<CancelPolicy, CancellationCreateResponse, CancelPolicyAuditor>();
        services.AddCommand<CancelPolicy, CancellationCreateResponse, CancelPolicyHandler>(CommandDescriptor.For("pol.Cancellation.create") with { SupportsDryRun = true });

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every POL-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.POL, "VALIDATION", 422, "Τα στοιχεία δεν είναι έγκυρα", "The data is not valid")
            .Describe("Ένα ή περισσότερα πεδία δεν πέρασαν τον έλεγχο· το errors[] αναφέρει το πεδίο και τον λόγο.", "One or more fields failed validation; errors[] names each field and the reason."),
        ErrorDefinition.For(ModuleCode.POL, "RETROACTIVE-MTPL", 422, "Η ασφάλιση αστικής ευθύνης δεν αρχίζει αναδρομικά", "Motor liability cover cannot start retroactively")
            .Describe("Νέα ασφάλιση αρχίζει τώρα ή αργότερα, χωρίς εξαίρεση.", "New business starts now or later, without override."),
        ErrorDefinition.For(ModuleCode.POL, "SEGMENT-INVARIANT", 409, "Η χρονική συνέπεια του συμβολαίου παραβιάζεται", "The policy timeline would be inconsistent")
            .Describe("Η εγγραφή θα επικαλυπτόταν με υπάρχουσα περίοδο ισχύος.", "The record would overlap an existing period of cover."),
        ErrorDefinition.For(ModuleCode.POL, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει στη νομική σας οντότητα.", "The record does not exist in your legal entity."),
        ErrorDefinition.For(ModuleCode.POL, "STALE", 409, "Η εργασία άλλαξε στο μεταξύ", "The job changed meanwhile")
            .Describe("Κάποιος άλλος άλλαξε την εργασία. Φορτώστε τη νεότερη έκδοση και επαναλάβετε.", "Someone else changed the job. Load the newer version and try again."),
        ErrorDefinition.For(ModuleCode.POL, "ILLEGAL-TRANSITION", 409, "Η ενέργεια δεν επιτρέπεται σε αυτή την κατάσταση", "The action is not allowed in this state")
            .Describe("Η κατάσταση της εργασίας δεν επιτρέπει αυτή την ενέργεια.", "The job's state does not allow this action."),
        ErrorDefinition.For(ModuleCode.POL, "EFFDATE-LIMIT", 422, "Η ημερομηνία έναρξης δεν επιτρέπεται", "The effective date is not allowed")
            .Describe("Νέα ασφάλιση δεν μπορεί να αρχίζει πριν από τώρα.", "New business cannot start before now."),
        ErrorDefinition.For(ModuleCode.POL, "PRODUCT-UNAVAILABLE", 503, "Το προϊόν δεν είναι διαθέσιμο", "The product is not available", retryable: true)
            .Describe("Δεν βρέθηκε έκδοση προϊόντος για την ημερομηνία και το κανάλι.", "No product version was found for the date and channel."),
        ErrorDefinition.For(ModuleCode.POL, "PRODUCER-INVALID", 422, "Ο κωδικός διαμεσολαβητή δεν είναι έγκυρος", "The producer code is not valid")
            .Describe("Ο διαμεσολαβητής δεν μπορεί να διαθέσει αυτό το προϊόν.", "The producer cannot write this product."),
        ErrorDefinition.For(ModuleCode.POL, "RATING", 422, "Η τιμολόγηση απέτυχε", "Rating failed")
            .Describe("Η τιμολόγηση δεν επέστρεψε χρήσιμο αποτέλεσμα. Ελέγξτε τα στοιχεία κινδύνου.", "Rating did not return a usable result. Check the risk data."),
        ErrorDefinition.For(ModuleCode.POL, "QUOTE-STALE", 409, "Η προσφορά δεν ισχύει πλέον", "The quote is no longer valid")
            .Describe("Η ισχύς της προσφοράς έληξε ή υπάρχει νεότερη έκδοση. Επανατιμολογήστε.", "The quote expired or a newer version exists. Requote it."),
        ErrorDefinition.For(ModuleCode.POL, "QUICK-QUOTE-NOT-BINDABLE", 422, "Η γρήγορη προσφορά δεν δεσμεύεται", "A quick quote cannot be bound")
            .Describe("Οι τιμές γρήγορης προσφοράς είναι ενδεικτικές. Ολοκληρώστε πλήρη προσφορά.", "Quick-quote prices are indicative. Complete a full quote."),
        ErrorDefinition.For(ModuleCode.POL, "HUMAN-CONFIRMATION-REQUIRED", 422, "Απαιτείται ρητή επιβεβαίωση", "Explicit confirmation is required")
            .Describe("Ο χρήστης πρέπει να επιβεβαιώσει ρητά τη σύναψη.", "The user must confirm the bind explicitly."),
        ErrorDefinition.For(ModuleCode.POL, "GATE-FAILED", 422, "Δεν πέρασε έλεγχος σύναψης", "A bind gate failed")
            .Describe("Ένας έλεγχος πριν από τη σύναψη δεν ολοκληρώθηκε.", "A check before binding could not be completed."),
        ErrorDefinition.For(ModuleCode.POL, "DEPENDENCY-UNAVAILABLE", 503, "Μια απαραίτητη υπηρεσία δεν είναι διαθέσιμη", "A required service is not available", retryable: true)
            .Describe("Η λειτουργία χρειάζεται υπηρεσία άλλης ενότητας που δεν έχει ακόμη συνδεθεί.", "The operation needs another module's service that is not wired yet."),
        ErrorDefinition.For(ModuleCode.POL, PolicyErrorNames.OutOfSequence, 422, "Η ενέργεια προηγείται της τελευταίας δεσμευμένης συναλλαγής του όρου", "The effective time is earlier than the term's latest bound transaction")
            .Describe("Η ενέργεια ισχύει από ημερομηνία πριν από την τελευταία δεσμευμένη συναλλαγή του όρου. Επιλέξτε μεταγενέστερη ημερομηνία ή ξεκινήστε από τη νεότερη κατάσταση.", "The action takes effect before the term's latest bound transaction. Choose a later date or start again from the latest state."),
        ErrorDefinition.For(ModuleCode.POL, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
