using CoreIns.Modules.Claims.Authority;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Events;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Modules.Claims.Services;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.Modules.Claims;

/// <summary>
/// Composition entry point of the Claims module (PRD-07 Claims). The Host calls <see cref="AddClaimsModule"/>; the migrate
/// job applies <see cref="Databases"/>. SL2-CLM-CORE builds the claims reference vertical: FNOL (staff channel) with cover
/// verified on the POL snapshot at the loss date, the claim / exposure / claimant / incident model and state machine,
/// search, close with the close guard. POL is called only through its generated in-process contract (D-ARC-16).
/// </summary>
public static class ClaimsModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "clm";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>
    /// The module database for the migrate job. Least privilege per table: claim, exposure, claimant and incident move
    /// (SELECT, INSERT, UPDATE); the FNOL snapshot is immutable (SELECT, INSERT; a trigger refuses UPDATE/DELETE for every
    /// role, REQ-CLM-044). Nothing gets DELETE.
    /// </summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
    [
        new(
            ModuleCode.CLM,
            Schema,
            connectionString => ModuleDbContextRegistration.CreateForMigration<ClaimsDbContext>(connectionString, Schema),
            appRole =>
            [
                $"REVOKE ALL ON ALL TABLES IN SCHEMA {Schema} FROM {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.claim, {Schema}.exposure, {Schema}.claimant, {Schema}.incident TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.fnol_snapshot TO {appRole}",

                // Claim financials (SL2-CLM-MONEY, D-ARC-34): the ledger lines are insert-only; the set header, line flags,
                // payments and the payee read model move.
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.reserve_line, {Schema}.transaction_set, {Schema}.claim_payment, {Schema}.payee_account_view TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.financial_transaction TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.set_approval TO {appRole}",
            ]),
    ];

    /// <summary>Registers the module's services: DbContext, commands, queries, in-process contracts, error definitions.</summary>
    public static IServiceCollection AddClaimsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddClaimsAuthorityTypes();

        services.AddOptions<ClaimsOptions>().Bind(configuration.GetSection(ClaimsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddModuleDbContext<ClaimsDbContext>(Schema);

        services.AddScoped<ClaimProtection>();
        services.AddScoped<ClaimReader>();
        services.AddScoped<FnolAssessment>();
        services.AddScoped<FnolValidation>();
        services.AddScoped<ICoverageSource, PolicySnapshotAdapter>();

        // Claim financials (SL2-CLM-MONEY): derived balances, the set lifecycle and the close guard over them (REQ-CLM-072/073).
        services.AddScoped<FinancialsReader>();
        services.AddScoped<SetLifecycle>();
        services.TryAddScoped<IClaimFinancialGuard, DerivedClaimFinancials>();

        services.AddScoped<IValidator<BuildTransactionSet>, BuildTransactionSetValidator>();
        services.AddCommandAuditor<BuildTransactionSet, TransactionSetBuildResponse, BuildTransactionSetAuditor>();
        services.AddCommand<BuildTransactionSet, TransactionSetBuildResponse, BuildTransactionSetHandler>(
            CommandDescriptor.For("clm.TransactionSet.build") with { SupportsDryRun = true });
        services.AddScoped<IValidator<SubmitTransactionSet>, SubmitTransactionSetValidator>();
        services.AddCommandAuditor<SubmitTransactionSet, TransactionSetSubmitResponse, SubmitTransactionSetAuditor>();
        services.AddCommand<SubmitTransactionSet, TransactionSetSubmitResponse, SubmitTransactionSetHandler>(
            CommandDescriptor.For("clm.TransactionSet.submit") with { SupportsDryRun = true });
        services.AddScoped<IValidator<CapturePayeeAccount>, CapturePayeeAccountValidator>();
        services.AddCommandAuditor<CapturePayeeAccount, PayeeAccountCaptureResponse, CapturePayeeAccountAuditor>();
        services.AddCommand<CapturePayeeAccount, PayeeAccountCaptureResponse, CapturePayeeAccountHandler>(CommandDescriptor.For("clm.PayeeAccount.capture"));

        // Internal commands run by the event handlers (the outbox marker makes them exactly-once; no idempotency key).
        services.AddCommandAuditor<ApplyApprovalDecision, ApprovalOutcome, ApplyApprovalDecisionAuditor>();
        services.AddCommand<ApplyApprovalDecision, ApprovalOutcome, ApplyApprovalDecisionHandler>(
            CommandDescriptor.For("clm.TransactionSet.applyDecision") with { RequiresIdempotencyKey = false, Idempotent = false });
        services.AddCommandAuditor<RecordDisbursementOutcome, string, RecordDisbursementOutcomeAuditor>();
        services.AddCommand<RecordDisbursementOutcome, string, RecordDisbursementOutcomeHandler>(
            CommandDescriptor.For("clm.Payment.recordDisbursement") with { RequiresIdempotencyKey = false, Idempotent = false });

        // Consumers (worker): PLT decisions on referred sets, BIL disbursement status (REQ-CLM-128).
        services.AddEventHandler<ApprovalDecidedV1, ApprovalDecidedHandler>(EventDescriptor.From(ApprovalDecidedV1.Descriptor), ApprovalDecidedHandler.Name, ModuleCode.CLM);
        services.AddEventHandler<DisbursementIssuedV1, DisbursementIssuedHandler>(
            EventDescriptor.From(DisbursementIssuedV1.Descriptor), DisbursementIssuedHandler.Name, ModuleCode.CLM);
        services.AddEventHandler<DisbursementClearedV1, DisbursementClearedHandler>(
            EventDescriptor.From(DisbursementClearedV1.Descriptor), DisbursementClearedHandler.Name, ModuleCode.CLM);

        services.AddScoped<IValidator<SubmitFnol>, SubmitFnolValidator>();
        services.AddCommandAuditor<SubmitFnol, FnolSubmitResponse, SubmitFnolAuditor>();
        services.AddCommand<SubmitFnol, FnolSubmitResponse, SubmitFnolHandler>(CommandDescriptor.For("clm.Fnol.submit") with { SupportsDryRun = true });

        services.AddScoped<IValidator<CloseClaim>, CloseClaimValidator>();
        services.AddCommandAuditor<CloseClaim, ClaimCloseResponse, CloseClaimAuditor>();
        services.AddCommand<CloseClaim, ClaimCloseResponse, CloseClaimHandler>(CommandDescriptor.For("clm.Claim.close") with { SupportsDryRun = true });

        services.AddScoped<IValidator<CreateExposure>, CreateExposureValidator>();
        services.AddCommandAuditor<CreateExposure, ExposureCreateResponse, CreateExposureAuditor>();
        services.AddCommand<CreateExposure, ExposureCreateResponse, CreateExposureHandler>(CommandDescriptor.For("clm.Exposure.create") with { SupportsDryRun = true });

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<IClaimsFnolService, ClaimsFnolService>();
        services.AddScoped<IClaimsClaimService, ClaimsClaimService>();
        services.AddScoped<IClaimsFinancialsService, ClaimsFinancialsService>();
        services.AddScoped<IClaimsTransactionSetService, ClaimsTransactionSetService>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every CLM-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.CLM, "FNOL-001", 422, "Λείπει υποχρεωτικό στοιχείο της αναγγελίας", "A mandatory FNOL field is missing")
            .Describe("Συμπληρώστε το πεδίο που αναφέρεται στο errors[] και υποβάλετε ξανά.", "Fill in the field named in errors[] and submit again."),
        ErrorDefinition.For(ModuleCode.CLM, "LOSS-DATE", 422, "Μη αποδεκτή ημερομηνία ζημίας ή αναγγελίας", "The loss or notice date is not acceptable")
            .Describe("Η ζημία δεν μπορεί να είναι μελλοντική ούτε μεταγενέστερη της αναγγελίας.", "The loss cannot be in the future or after the notice date."),
        ErrorDefinition.For(ModuleCode.CLM, "POLICY-UNVERIFIED", 422, "Το ασφαλιστήριο δεν επαληθεύτηκε", "The policy could not be verified")
            .Describe("Το ασφαλιστήριο δεν βρέθηκε ή δεν δόθηκε στιγμιότυπο κατά την ημερομηνία ζημίας.", "The policy was not found or no snapshot exists at the loss date."),
        ErrorDefinition.For(ModuleCode.CLM, "DUPLICATE-CANDIDATES", 409, "Υπάρχουν πιθανές διπλές ζημίες", "Probable duplicate claims exist")
            .Describe("Συνδέστε με υπάρχουσα ζημία ή συνεχίστε δίνοντας αιτιολογία (duplicateDecision).", "Link to an existing claim or override with a reason (duplicateDecision)."),
        ErrorDefinition.For(ModuleCode.CLM, "DEPENDENCY-UNAVAILABLE", 503, "Μια απαραίτητη υπηρεσία δεν είναι διαθέσιμη", "A required service is not available", retryable: true)
            .Describe("Η υπηρεσία ασφαλιστηρίων δεν απάντησε. Δοκιμάστε ξανά σε λίγο.", "The policy service did not answer. Try again shortly."),
        ErrorDefinition.For(ModuleCode.CLM, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει στη νομική σας οντότητα.", "The record does not exist in your legal entity."),
        ErrorDefinition.For(ModuleCode.CLM, "STALE", 409, "Η ζημία άλλαξε στο μεταξύ", "The claim changed meanwhile", retryable: true)
            .Describe("Κάποιος άλλος άλλαξε τη ζημία. Φορτώστε τη νεότερη έκδοση και επαναλάβετε.", "Someone else changed the claim. Load the newer version and try again."),
        ErrorDefinition.For(ModuleCode.CLM, "ILLEGAL-TRANSITION", 422, "Η ενέργεια δεν επιτρέπεται σε αυτή την κατάσταση", "The action is not allowed in this state")
            .Describe("Η κατάσταση της ζημίας δεν επιτρέπει αυτή την ενέργεια.", "The claim's state does not allow this action."),
        ErrorDefinition.For(ModuleCode.CLM, "CLOSE-GUARD", 422, "Η ζημία δεν μπορεί να κλείσει ακόμη", "The claim cannot close yet")
            .Describe("Υπάρχει ανοιχτό απόθεμα ή εκκρεμής πληρωμή σε έκθεση· αποδεσμεύστε ή τακτοποιήστε πρώτα.", "An exposure has an open reserve or a pending payment; release or settle it first."),
        ErrorDefinition.For(ModuleCode.CLM, "EXPOSURE-DUPLICATE", 409, "Υπάρχει ήδη ανοιχτή έκθεση ζημίας", "An open exposure already exists")
            .Describe("Ίδια κάλυψη, αιτών και συμβάν· δώστε αιτιολογία για δεύτερη έκθεση.", "Same coverage, claimant and incident; give a reason to add a second exposure."),
        ErrorDefinition.For(ModuleCode.CLM, "SEARCH-CRITERIA", 422, "Ανεπαρκή κριτήρια αναζήτησης", "Insufficient search criteria")
            .Describe("Δώστε αριθμό ζημίας, αριθμό ασφαλιστηρίου ή ασφαλισμένο.", "Give a claim number, a policy number or an insured party."),
        ErrorDefinition.For(ModuleCode.CLM, "RESERVE-REASON", 422, "Λείπει η αιτιολογία της μεταβολής αποθέματος", "A reserve change needs a reason")
            .Describe("Δώστε κωδικό αιτιολογίας για κάθε χειροκίνητη μεταβολή αποθέματος.", "Give a reason code for every manual reserve change."),
        ErrorDefinition.For(ModuleCode.CLM, "NOT-PAYABLE", 422, "Η πληρωμή δεν επιτρέπεται", "The payment is not allowed")
            .Describe("Δείτε τους λόγους (reasons): ασφαλιστήριο, κάλυψη έκθεσης, λογαριασμός δικαιούχου.", "See the reasons: policy, exposure cover, payee account."),
        ErrorDefinition.For(ModuleCode.CLM, "DUPLICATE-PAYMENT", 409, "Υπάρχει ήδη ίδια πληρωμή", "The same payment exists already")
            .Describe("Ίδιο ποσό στον ίδιο λογαριασμό στην ίδια ζημιά.", "Same amount to the same account on the same claim."),
        ErrorDefinition.For(ModuleCode.CLM, "PAYMENT-EXCEEDS-RESERVE", 422, "Η πληρωμή υπερβαίνει το ανοικτό απόθεμα", "The payment exceeds the open reserve")
            .Describe("Αυξήστε πρώτα το απόθεμα της γραμμής.", "Increase the line's reserve first."),
        ErrorDefinition.For(ModuleCode.CLM, "SET-STALE", 409, "Το σύνολο κινήσεων δεν είναι πλέον έγκυρο", "The transaction set is stale", retryable: true)
            .Describe("Κάτι άλλαξε από τη δημιουργία του· δημιουργήστε το ξανά.", "Something changed since it was built; build it again."),
        ErrorDefinition.For(ModuleCode.CLM, "AUTHORITY", 403, "Εκτός ορίων εξουσιοδότησης", "Outside your authority")
            .Describe("Μια κίνηση υπερβαίνει κάθε διαθέσιμο όριο εξουσιοδότησης.", "A transaction exceeds every available authority limit."),
        ErrorDefinition.For(ModuleCode.CLM, "PAYEE-NOT-ON-CLAIM", 422, "Ο δικαιούχος δεν συμμετέχει στη ζημιά", "The payee is not on the claim")
            .Describe("Ο δικαιούχος πρέπει να είναι ο ασφαλισμένος ή αιτών της ζημιάς.", "The payee must be the insured or a claimant of the claim."),
        ErrorDefinition.For(ModuleCode.CLM, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
    ];
}
