using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Events;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Platform;
using CoreIns.Platform.Approvals;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing;

/// <summary>
/// Composition entry point of the Billing module (PRD-06 Billing and collections). The Host calls
/// <see cref="AddBillingModule"/>; the migrate job applies <see cref="Databases"/>. SL-BIL builds the E2E-01 path:
/// charge intake from POL, billing account and ANNUAL plan instance, invoice with a gapless number, the fiscal request to
/// CMP, payment receipt and allocation, and the append-only billing sub-ledger with <c>BillingEntryPosted</c> for FIN.
/// </summary>
public static class BillingModule
{
    /// <summary>PostgreSQL schema owned by this module. No other module reads it.</summary>
    public const string Schema = "bil";

    /// <summary>All PostgreSQL schemas owned by this module, created by the migrate job.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [Schema];

    /// <summary>
    /// The module database for the migrate job. Least privilege per table: working rows get SELECT, INSERT, UPDATE; the
    /// sub-ledger (<c>ledger_entry</c>, <c>ledger_line</c>) and <c>allocation</c> are append-only and get SELECT, INSERT
    /// (REQ-BIL-281; triggers refuse UPDATE/DELETE/TRUNCATE for every role as well); the chart and the rule table are
    /// read-only to the app (maker-checker changes are a later package). Nothing gets DELETE.
    /// </summary>
    public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
    [
        new(
            ModuleCode.BIL,
            Schema,
            connectionString => ModuleDbContextRegistration.CreateForMigration<BillingDbContext>(connectionString, Schema),
            appRole =>
            [
                $"REVOKE ALL ON ALL TABLES IN SCHEMA {Schema} FROM {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.billing_account, {Schema}.plan_instance, {Schema}.charge, {Schema}.invoice, {Schema}.invoice_item, {Schema}.receipt, {Schema}.intake_exception TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.payee_account, {Schema}.disbursement, {Schema}.refund TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.refund_credit, {Schema}.refund_netting TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.allocation, {Schema}.credit_application, {Schema}.ledger_entry, {Schema}.ledger_line TO {appRole}",
                $"GRANT SELECT ON {Schema}.ledger_account, {Schema}.ledger_rule TO {appRole}",
                $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {Schema} TO {appRole}",
            ]),
    ];

    /// <summary>Registers the module's services: DbContext, commands, queries, event handlers, in-process contracts, errors.</summary>
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BillingOptions>().Bind(configuration.GetSection(BillingOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddModuleDbContext<BillingDbContext>(Schema);

        services.AddScoped<LedgerWriter>();
        services.AddScoped<TermBilling>();
        services.AddScoped<Allocator>();
        services.AddScoped<BillingReader>();

        // Internal commands run by the event handlers (audited; no Idempotency-Key: they are idempotent on the charge,
        // term and fiscal document themselves).
        var intake = CommandDescriptor.For("bil.Charge.intake") with { RequiresIdempotencyKey = false, Idempotent = false };
        services.AddCommandAuditor<IntakeCharge, IntakeOutcome, IntakeChargeAuditor>();
        services.AddCommand<IntakeCharge, IntakeOutcome, IntakeChargeHandler>(intake);
        services.AddCommandAuditor<AttachTerm, IntakeOutcome, AttachTermAuditor>();
        services.AddCommand<AttachTerm, IntakeOutcome, AttachTermHandler>(
            CommandDescriptor.For("bil.BillingAccount.attachTerm") with { RequiresIdempotencyKey = false, Idempotent = false });
        services.AddCommandAuditor<StopTermBilling, IntakeOutcome, StopTermBillingAuditor>();
        services.AddCommand<StopTermBilling, IntakeOutcome, StopTermBillingHandler>(
            CommandDescriptor.For("bil.Term.stopBilling") with { RequiresIdempotencyKey = false, Idempotent = false });
        services.AddCommandAuditor<AttachRenewalTerm, IntakeOutcome, AttachRenewalTermAuditor>();
        services.AddCommand<AttachRenewalTerm, IntakeOutcome, AttachRenewalTermHandler>(
            CommandDescriptor.For("bil.BillingAccount.attachRenewalTerm") with { RequiresIdempotencyKey = false, Idempotent = false });
        services.AddCommandAuditor<RecordFiscalOutcome, int, RecordFiscalOutcomeAuditor>();
        services.AddCommand<RecordFiscalOutcome, int, RecordFiscalOutcomeHandler>(
            CommandDescriptor.For("bil.Invoice.recordFiscal") with { RequiresIdempotencyKey = false, Idempotent = false });

        // API commands.
        services.AddScoped<IValidator<TakePayment>, TakePaymentValidator>();
        services.AddCommandAuditor<TakePayment, PaymentTakeResponse, TakePaymentAuditor>();
        services.AddCommand<TakePayment, PaymentTakeResponse, TakePaymentHandler>(CommandDescriptor.For("bil.Payment.take") with { SupportsDryRun = true });
        services.AddScoped<IValidator<AllocateReceipt>, AllocateReceiptValidator>();
        services.AddCommandAuditor<AllocateReceipt, AllocationAllocateResponse, AllocateReceiptAuditor>();
        services.AddCommand<AllocateReceipt, AllocationAllocateResponse, AllocateReceiptHandler>(
            CommandDescriptor.For("bil.Allocation.allocate") with { SupportsDryRun = true });

        // SL2-BIL-DISB: payee accounts (IBAN P2) and the disbursement service for CLM claim payments. Both commands are
        // idempotent on the caller's key; their results carry the masked IBAN only, never the IBAN (REQ-BIL-338).
        services.AddScoped<PayeeProtection>();
        services.AddScoped<DisbursementReader>();
        services.AddScoped<IValidator<CreatePayeeAccount>, CreatePayeeAccountValidator>();
        services.AddCommandAuditor<CreatePayeeAccount, PayeeAccountCreateResponse, CreatePayeeAccountAuditor>();
        services.AddCommand<CreatePayeeAccount, PayeeAccountCreateResponse, CreatePayeeAccountHandler>(
            CommandDescriptor.For("bil.PayeeAccount.create") with { SupportsDryRun = true });
        services.AddScoped<IValidator<RequestDisbursement>, RequestDisbursementValidator>();
        services.AddCommandAuditor<RequestDisbursement, DisbursementRequestResponse, RequestDisbursementAuditor>();
        services.AddCommand<RequestDisbursement, DisbursementRequestResponse, RequestDisbursementHandler>(
            CommandDescriptor.For("bil.Disbursement.request") with { SupportsDryRun = true });

        // BIL.REFUND approvals are decided by bil.Refund.decide (it also executes the refund), never in the generic approvals inbox.
        services.AddOwnerDecidedApprovalType(DisbursementApproval.RefundType);

        // SL3-BIL-REFUND: refunds of the credit balance (REQ-BIL-007, -181…-191), paid through the disbursement service (BIL_REFUND).
        services.AddScoped<RefundCredits>();
        services.AddScoped<RefundReader>();
        services.AddScoped<RefundWorkflow>();
        services.AddScoped<IValidator<ProposeRefund>, ProposeRefundValidator>();
        services.AddCommandAuditor<ProposeRefund, RefundProposeResponse, ProposeRefundAuditor>();
        services.AddCommand<ProposeRefund, RefundProposeResponse, ProposeRefundHandler>(CommandDescriptor.For("bil.Refund.propose") with { SupportsDryRun = true });
        services.AddScoped<IValidator<DecideRefund>, DecideRefundValidator>();
        services.AddCommandAuditor<DecideRefund, RefundDecideResponse, DecideRefundAuditor>();
        services.AddCommand<DecideRefund, RefundDecideResponse, DecideRefundHandler>(CommandDescriptor.For("bil.Refund.decide") with { SupportsDryRun = true });
        services.AddScoped<IValidator<ResubmitRefund>, ResubmitRefundValidator>();
        services.AddCommandAuditor<ResubmitRefund, RefundResubmitResponse, ResubmitRefundAuditor>();
        services.AddCommand<ResubmitRefund, RefundResubmitResponse, ResubmitRefundHandler>(CommandDescriptor.For("bil.Refund.resubmit") with { SupportsDryRun = true });

        // Consumers (worker): POL charges and bind, CMP fiscal outcomes.
        services.AddEventHandler<ChargeDeltaEmittedV1, ChargeDeltaEmittedHandler>(
            EventDescriptor.From(ChargeDeltaEmittedV1.Descriptor), ChargeDeltaEmittedHandler.Name, ModuleCode.BIL);
        services.AddEventHandler<PolicyBoundV1, PolicyBoundHandler>(EventDescriptor.From(PolicyBoundV1.Descriptor), PolicyBoundHandler.Name, ModuleCode.BIL);
        services.AddEventHandler<PolicyCancelledV1, PolicyCancelledHandler>(
            EventDescriptor.From(PolicyCancelledV1.Descriptor), PolicyCancelledHandler.Name, ModuleCode.BIL);
        services.AddEventHandler<RenewalBoundV1, RenewalBoundHandler>(
            EventDescriptor.From(RenewalBoundV1.Descriptor), RenewalBoundHandler.Name, ModuleCode.BIL);
        services.AddEventHandler<FiscalDocRegisteredV1, FiscalDocRegisteredHandler>(
            EventDescriptor.From(FiscalDocRegisteredV1.Descriptor), FiscalDocRegisteredHandler.Name, ModuleCode.BIL);
        services.AddEventHandler<FiscalDocRejectedV1, FiscalDocRejectedHandler>(
            EventDescriptor.From(FiscalDocRejectedV1.Descriptor), FiscalDocRejectedHandler.Name, ModuleCode.BIL);

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<IBillingBillingAccountService, BillingAccountService>();
        services.AddScoped<IBillingInvoiceService, InvoiceService>();
        services.AddScoped<IBillingPaymentService, PaymentService>();
        services.AddScoped<IBillingReceiptService, ReceiptService>();
        services.AddScoped<IBillingPayeeAccountService, PayeeAccountService>();
        services.AddScoped<IBillingDisbursementService, DisbursementService>();
        services.AddScoped<IBillingRefundService, RefundService>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    /// <summary>Status, bilingual title and description of every BIL-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.BIL, "NOT-FOUND", 404, "Δεν βρέθηκε", "Not found")
            .Describe("Η εγγραφή δεν υπάρχει στη νομική σας οντότητα.", "The record does not exist in your legal entity."),
        ErrorDefinition.For(ModuleCode.BIL, "CURRENCY", 422, "Λάθος νόμισμα", "Wrong currency")
            .Describe("Το ποσό πρέπει να είναι στο νόμισμα του λογαριασμού χρέωσης.", "The amount must be in the billing account's currency."),
        ErrorDefinition.For(ModuleCode.BIL, "AMOUNT-MISMATCH", 422, "Το ποσό δεν αντιστοιχεί στο ανοιχτό υπόλοιπο", "The amount does not match the open balance")
            .Describe("Κατανέμεται μόνο το ακριβές ανοιχτό ποσό της ειδοποίησης πληρωμής· το υπόλοιπο μένει αδιάθετο.", "Only the payment notice's exact open amount is allocated; the rest stays unapplied."),
        ErrorDefinition.For(ModuleCode.BIL, "OVER-ALLOCATION", 422, "Υπέρβαση κατανομής", "Over-allocation")
            .Describe("Η κατανομή υπερβαίνει την είσπραξη ή το ανοιχτό ποσό.", "The allocation exceeds the receipt or the open amount."),
        ErrorDefinition.For(ModuleCode.BIL, "STALE", 409, "Η εγγραφή άλλαξε στο μεταξύ", "The record changed meanwhile")
            .Describe("Κάποιος άλλος άλλαξε την εγγραφή. Φορτώστε τη νεότερη έκδοση και επαναλάβετε.", "Someone else changed the record. Load the newer version and try again."),
        ErrorDefinition.For(ModuleCode.BIL, "METHOD-NOT-ALLOWED", 422, "Η πληρωμή δεν επιτρέπεται", "The payment is not allowed")
            .Describe("Ο λογαριασμός χρέωσης δεν δέχεται πληρωμές σε αυτή την κατάσταση.", "The billing account does not accept payments in this state."),
        ErrorDefinition.For(ModuleCode.BIL, "NO-RULE", 422, "Δεν υπάρχει κανόνας λογιστικής εγγραφής", "No billing-ledger rule")
            .Describe("Κανένας κανόνας του βοηθητικού καθολικού δεν αντιστοιχεί· δεν γίνεται εγγραφή σε προεπιλεγμένο λογαριασμό.", "No sub-ledger rule matches; nothing is posted to a default account."),
        ErrorDefinition.For(ModuleCode.BIL, "NOT-AVAILABLE", 501, "Η λειτουργία δεν είναι ακόμη διαθέσιμη", "The operation is not available yet")
            .Describe("Η λειτουργία ανήκει σε επόμενο πακέτο εργασιών.", "The operation belongs to a later work package."),
        ErrorDefinition.For(ModuleCode.BIL, "IBAN-INVALID", 422, "Μη έγκυρο IBAN", "Invalid IBAN")
            .Describe("Το IBAN δεν έχει έγκυρη μορφή ή ψηφία ελέγχου (ISO 13616).", "The IBAN does not have a valid structure or check digits (ISO 13616)."),
        ErrorDefinition.For(ModuleCode.BIL, "CHARSET", 422, "Μη επιτρεπτοί χαρακτήρες", "Characters not allowed")
            .Describe("Το όνομα δικαιούχου περιέχει χαρακτήρες που δεν μεταφέρει μια τραπεζική εντολή.", "The holder name contains characters a bank transfer cannot carry."),
        ErrorDefinition.For(ModuleCode.BIL, "SOURCE", 422, "Μη καταχωρισμένη πηγή πληρωμής", "Source not registered")
            .Describe("Η πηγή της πληρωμής δεν είναι στο μητρώο πηγών.", "The payment source is not in the source register."),
        ErrorDefinition.For(ModuleCode.BIL, "APPROVAL-MISMATCH", 422, "Η πληρωμή δεν αντιστοιχεί στην έγκριση", "The payment does not match its approval")
            .Describe("Το περιεχόμενο της αίτησης διαφέρει από αυτό που εγκρίθηκε· δεν πληρώνεται.", "The request differs from what was approved; it is not paid."),
        ErrorDefinition.For(ModuleCode.BIL, "PAYEE-ACCOUNT", 422, "Μη κατάλληλος λογαριασμός δικαιούχου", "Payee account not usable")
            .Describe("Ο λογαριασμός δεν ανήκει στον δικαιούχο, δεν είναι για αυτόν τον σκοπό ή δεν είναι ενεργός.", "The account is not the payee's, not for this purpose or not active."),
        ErrorDefinition.For(ModuleCode.BIL, "VOP-HOLD", 409, "Εκκρεμεί επαλήθευση δικαιούχου", "Payee verification pending")
            .Describe("Η επαλήθευση δικαιούχου δεν επιβεβαίωσε τον λογαριασμό· η πληρωμή κρατείται.", "Verification of payee did not confirm the account; the payment is held."),
        ErrorDefinition.For(ModuleCode.BIL, "COOLING-OFF", 409, "Πρόσφατη αλλαγή λογαριασμού", "Recent bank account change")
            .Describe("Ο λογαριασμός άλλαξε πρόσφατα· απαιτείται έγκριση από δεύτερο χρήστη.", "The account changed recently; a second person must approve."),
        ErrorDefinition.For(ModuleCode.BIL, "PAYEE-BLOCKED", 409, "Ο δικαιούχος δεν ελέγχθηκε επιτυχώς", "Payee not cleared")
            .Describe("Ο έλεγχος κυρώσεων δεν ήταν καθαρός· η πληρωμή δεν γίνεται.", "Sanctions screening was not clear; the payment is not made."),
        ErrorDefinition.For(ModuleCode.BIL, "SCREENING-UNAVAILABLE", 503, "Ο έλεγχος κυρώσεων δεν είναι διαθέσιμος", "Sanctions screening unavailable")
            .Describe("Ο έλεγχος κυρώσεων δεν ολοκληρώθηκε· η πληρωμή δεν γίνεται. Δοκιμάστε ξανά αργότερα.", "Sanctions screening did not complete; the payment is not made. Try again later."),
        ErrorDefinition.For(ModuleCode.BIL, "NO-CREDIT", 422, "Δεν υπάρχει πιστωτικό υπόλοιπο προς επιστροφή", "No credit left to refund")
            .Describe("Ο λογαριασμός δεν έχει πιστωτικό υπόλοιπο ή αυτό συμψηφίστηκε με ανοιχτές ειδοποιήσεις πληρωμής.", "The account has no credit, or it is fully set against open invoices."),
        ErrorDefinition.For(ModuleCode.BIL, "REFUND-OPEN", 409, "Υπάρχει ήδη ανοιχτή επιστροφή", "A refund is already open")
            .Describe("Ένας λογαριασμός χρέωσης έχει μία ανοιχτή επιστροφή τη φορά· αποφασίστε πρώτα για αυτήν.", "A billing account has one open refund at a time; decide it first."),
        ErrorDefinition.For(ModuleCode.BIL, "REFUND-BELOW-MINIMUM", 422, "Η επιστροφή είναι κάτω από το ελάχιστο", "The refund is below the minimum")
            .Describe("Το ποσό προς επιστροφή είναι μικρότερο από το ελάχιστο· το πιστωτικό μένει στον λογαριασμό.", "The amount to refund is below the minimum; the credit stays on the account."),
        ErrorDefinition.For(ModuleCode.BIL, "REFUND-STATE", 409, "Η επιστροφή δεν είναι στην κατάλληλη κατάσταση", "The refund is not in the right state")
            .Describe("Η ενέργεια δεν επιτρέπεται στην τρέχουσα κατάσταση της επιστροφής.", "The action is not allowed in the refund's current state."),
        ErrorDefinition.For(ModuleCode.BIL, "SOD", 403, "Διαχωρισμός καθηκόντων", "Segregation of duties")
            .Describe("Ο αιτών, όποιος επεξεργάστηκε την επιστροφή ή όποιος άλλαξε τον λογαριασμό δικαιούχου δεν μπορεί να την εγκρίνει.", "The requester, an editor of the refund or the person who changed the payee account cannot approve it."),
        ErrorDefinition.For(ModuleCode.BIL, "PAYEE-UNVERIFIED", 422, "Ο λογαριασμός δικαιούχου δεν έχει επαληθευτεί", "Payee account not verified")
            .Describe("Η επιστροφή πληρώνεται μόνο σε επαληθευμένο λογαριασμό του πληρωτή.", "A refund is paid only to the payer's verified bank account."),
        ErrorDefinition.For(ModuleCode.BIL, "NOT-PERMITTED", 403, "Δεν έχετε εξουσιοδότηση για αυτό το ποσό", "No authority for this amount")
            .Describe("Δεν υπάρχει εξουσιοδότηση επιστροφής για αυτό το ποσό.", "No refund authority covers this amount."),
        ErrorDefinition.For(ModuleCode.BIL, "DUPLICATE", 409, "Πιθανή διπλή πληρωμή", "Possible duplicate payment")
            .Describe("Υπάρχει ήδη πληρωμή για την ίδια πηγή ή με τον ίδιο λογαριασμό, ποσό και αναφορά.", "A payment already exists for the same source or with the same account, amount and reference."),
    ];
}
