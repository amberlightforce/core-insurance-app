using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Events;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Documents.Contracts.Events;
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
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.dsar_restriction TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.payee_account, {Schema}.disbursement, {Schema}.refund TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.delinquency_case TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.delinquency_contact TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.refund_credit, {Schema}.refund_netting TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.respread TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.receivable, {Schema}.receivable_allocation, {Schema}.allocation, {Schema}.credit_application, {Schema}.ledger_entry, {Schema}.ledger_line TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.migration_receivable, {Schema}.migration_mandate TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.suspense_item TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.payment_instrument, {Schema}.mandate TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.levy_payable TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.disbursement_batch, {Schema}.disbursement_batch_item TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.bank_statement, {Schema}.bank_statement_line TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.write_off TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.write_off_line TO {appRole}",
                $"GRANT SELECT, INSERT ON {Schema}.dd_collection TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE ON {Schema}.dd_item TO {appRole}",
                $"GRANT SELECT, INSERT, UPDATE, DELETE ON {Schema}.down_payment TO {appRole}",
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
        services.AddSingleton<CoreIns.SharedKernel.Dsar.IDsarParticipant, CoreIns.Modules.Billing.Services.Dsar.BillingDsarParticipant>();

        services.AddScoped<LedgerWriter>();
        services.AddScoped<CoreIns.Modules.Migration.Contracts.Spi.IMigrationImporter, MigrationImport.BillingMigrationImporter>();
        services.AddScoped<CoreIns.Modules.Migration.Contracts.Spi.IMigrationReverser, MigrationImport.BillingMigrationReverser>();
        services.AddScoped<CoreIns.Modules.Migration.Contracts.Spi.IMigrationReconciliationReader, MigrationImport.BillingMigrationReconciliationReader>();
        services.AddScoped<TermBilling>();
        services.AddScoped<Allocator>();
        services.AddScoped<BillingReader>();
        services.AddScoped<ReceivableReader>();
        services.AddScoped<ReceivableMatching>();
        services.AddScoped<IBillingReceivableService, ReceivableService>();
        services.AddScoped<IValidator<RegisterReceivable>, RegisterReceivableValidator>();
        services.AddCommandAuditor<RegisterReceivable, ReceivableRegisterResponse, RegisterReceivableAuditor>();
        services.AddCommand<RegisterReceivable, ReceivableRegisterResponse, RegisterReceivableHandler>(CommandDescriptor.For("bil.Receivable.register") with { SupportsDryRun = true });

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
        // BO-BIL-07: receipt list and the reversal of allocations (unallocate, reallocate).
        services.AddScoped<ReceiptReader>();
        services.AddScoped<AllocationReverser>();
        services.AddScoped<IValidator<UnallocateReceipt>, UnallocateReceiptValidator>();
        services.AddCommandAuditor<UnallocateReceipt, AllocationUnallocateResponse, AllocationReversalAuditor<UnallocateReceipt, AllocationUnallocateResponse>>();
        services.AddCommand<UnallocateReceipt, AllocationUnallocateResponse, UnallocateReceiptHandler>(CommandDescriptor.For("bil.Allocation.unallocate") with { SupportsDryRun = true });
        services.AddScoped<IValidator<ReallocateReceipt>, ReallocateReceiptValidator>();
        services.AddCommandAuditor<ReallocateReceipt, AllocationReallocateResponse, AllocationReversalAuditor<ReallocateReceipt, AllocationReallocateResponse>>();
        services.AddCommand<ReallocateReceipt, AllocationReallocateResponse, ReallocateReceiptHandler>(CommandDescriptor.For("bil.Allocation.reallocate") with { SupportsDryRun = true });

        // BO-BIL-13: write-offs (maker-checker, tolerance) and money transfers between accounts of one payer.
        services.AddScoped<WriteOffReader>();
        services.AddScoped<WriteOffPoster>();
        services.AddScoped<IValidator<ProposeWriteOff>, ProposeWriteOffValidator>();
        services.AddCommandAuditor<ProposeWriteOff, WriteOffProposeResponse, WriteOffAuditor<ProposeWriteOff, WriteOffProposeResponse>>();
        services.AddCommand<ProposeWriteOff, WriteOffProposeResponse, ProposeWriteOffHandler>(CommandDescriptor.For("bil.WriteOff.propose") with { SupportsDryRun = true });
        services.AddScoped<IValidator<DecideWriteOff>, DecideWriteOffValidator>();
        services.AddCommandAuditor<DecideWriteOff, WriteOffDecideResponse, WriteOffAuditor<DecideWriteOff, WriteOffDecideResponse>>();
        services.AddCommand<DecideWriteOff, WriteOffDecideResponse, DecideWriteOffHandler>(CommandDescriptor.For("bil.WriteOff.decide") with { SupportsDryRun = true });
        services.AddCommandAuditor<PostWriteOff, WriteOffPostResponse, WriteOffAuditor<PostWriteOff, WriteOffPostResponse>>();
        services.AddCommand<PostWriteOff, WriteOffPostResponse, PostWriteOffHandler>(CommandDescriptor.For("bil.WriteOff.post") with { SupportsDryRun = true });
        services.AddScoped<IValidator<TransferMoney>, TransferMoneyValidator>();
        services.AddCommandAuditor<TransferMoney, TransferMoneyResponse, TransferMoneyAuditor>();
        services.AddCommand<TransferMoney, TransferMoneyResponse, TransferMoneyHandler>(CommandDescriptor.For("bil.Transfer.money") with { SupportsDryRun = true });

        // BO-BIL-03: payment plan catalogue, select/change and the billing preview.
        services.AddScoped<PaymentPlanReader>();
        services.AddScoped<IValidator<SelectPaymentPlan>, SelectPaymentPlanValidator>();
        services.AddCommandAuditor<SelectPaymentPlan, PaymentPlanSelectResponse, PaymentPlanAuditor<SelectPaymentPlan, PaymentPlanSelectResponse>>();
        services.AddCommand<SelectPaymentPlan, PaymentPlanSelectResponse, SelectPaymentPlanHandler>(CommandDescriptor.For("bil.PaymentPlan.select") with { SupportsDryRun = true });
        services.AddScoped<IValidator<ChangePaymentPlan>, ChangePaymentPlanValidator>();
        services.AddCommandAuditor<ChangePaymentPlan, PaymentPlanChangeResponse, PaymentPlanAuditor<ChangePaymentPlan, PaymentPlanChangeResponse>>();
        services.AddCommand<ChangePaymentPlan, PaymentPlanChangeResponse, ChangePaymentPlanHandler>(CommandDescriptor.For("bil.PaymentPlan.change") with { SupportsDryRun = true });

        // BO-BIL-04: down-payment bind gate.
        services.AddScoped<DownPaymentService>();
        services.AddScoped<IValidator<InitiateDownPayment>, InitiateDownPaymentValidator>();
        services.AddCommandAuditor<InitiateDownPayment, DownPaymentInitiateResponse, InitiateDownPaymentAuditor>();
        services.AddCommand<InitiateDownPayment, DownPaymentInitiateResponse, InitiateDownPaymentHandler>(CommandDescriptor.For("bil.DownPayment.initiate") with { SupportsDryRun = true });
        services.AddScoped<IBillingDownPaymentService, DownPaymentContractService>();

        // BO-BIL-01: account lifecycle (search, suspend/resume, close, reopen, change payer).
        services.AddScoped<BillingAccountSearchReader>();
        services.AddScoped<IValidator<UpdateBillingAccount>, UpdateBillingAccountValidator>();
        services.AddCommandAuditor<UpdateBillingAccount, BillingAccountUpdateResponse, BillingAccountAuditor<UpdateBillingAccount, BillingAccountUpdateResponse>>();
        services.AddCommand<UpdateBillingAccount, BillingAccountUpdateResponse, UpdateBillingAccountHandler>(CommandDescriptor.For("bil.BillingAccount.update") with { SupportsDryRun = true });
        services.AddScoped<IValidator<CloseBillingAccount>, CloseBillingAccountValidator>();
        services.AddCommandAuditor<CloseBillingAccount, BillingAccountCloseResponse, BillingAccountAuditor<CloseBillingAccount, BillingAccountCloseResponse>>();
        services.AddCommand<CloseBillingAccount, BillingAccountCloseResponse, CloseBillingAccountHandler>(CommandDescriptor.For("bil.BillingAccount.close") with { SupportsDryRun = true });
        services.AddScoped<IValidator<ReopenBillingAccount>, ReopenBillingAccountValidator>();
        services.AddCommandAuditor<ReopenBillingAccount, BillingAccountReopenResponse, BillingAccountAuditor<ReopenBillingAccount, BillingAccountReopenResponse>>();
        services.AddCommand<ReopenBillingAccount, BillingAccountReopenResponse, ReopenBillingAccountHandler>(CommandDescriptor.For("bil.BillingAccount.reopen"));
        services.AddScoped<IValidator<ChangeBillingAccountPayer>, ChangeBillingAccountPayerValidator>();
        services.AddCommandAuditor<ChangeBillingAccountPayer, BillingAccountChangePayerResponse, BillingAccountAuditor<ChangeBillingAccountPayer, BillingAccountChangePayerResponse>>();
        services.AddCommand<ChangeBillingAccountPayer, BillingAccountChangePayerResponse, ChangeBillingAccountPayerHandler>(CommandDescriptor.For("bil.BillingAccount.changePayer") with { SupportsDryRun = true });

        // BO-BIL-06a: payer instruments (IBAN P2) and SEPA mandates.
        services.AddScoped<MandateReader>();
        services.AddScoped<IValidator<AddPaymentInstrument>, AddPaymentInstrumentValidator>();
        services.AddCommandAuditor<AddPaymentInstrument, PaymentInstrumentAddResponse, MandateAuditor<AddPaymentInstrument, PaymentInstrumentAddResponse>>();
        services.AddCommand<AddPaymentInstrument, PaymentInstrumentAddResponse, AddPaymentInstrumentHandler>(CommandDescriptor.For("bil.PaymentInstrument.add") with { SupportsDryRun = true });
        services.AddScoped<IValidator<RemovePaymentInstrument>, RemovePaymentInstrumentValidator>();
        services.AddCommandAuditor<RemovePaymentInstrument, PaymentInstrumentRemoveResponse, MandateAuditor<RemovePaymentInstrument, PaymentInstrumentRemoveResponse>>();
        services.AddCommand<RemovePaymentInstrument, PaymentInstrumentRemoveResponse, RemovePaymentInstrumentHandler>(CommandDescriptor.For("bil.PaymentInstrument.remove") with { SupportsDryRun = true });
        services.AddScoped<IValidator<CreateMandate>, CreateMandateValidator>();
        services.AddCommandAuditor<CreateMandate, MandateCreateResponse, MandateAuditor<CreateMandate, MandateCreateResponse>>();
        services.AddCommand<CreateMandate, MandateCreateResponse, CreateMandateHandler>(CommandDescriptor.For("bil.Mandate.create") with { SupportsDryRun = true });
        services.AddScoped<IValidator<SignMandate>, SignMandateValidator>();
        services.AddCommandAuditor<SignMandate, MandateSignResponse, MandateAuditor<SignMandate, MandateSignResponse>>();
        services.AddCommand<SignMandate, MandateSignResponse, SignMandateHandler>(CommandDescriptor.For("bil.Mandate.sign") with { SupportsDryRun = true });
        services.AddScoped<IValidator<AmendMandate>, AmendMandateValidator>();
        services.AddCommandAuditor<AmendMandate, MandateAmendResponse, MandateAuditor<AmendMandate, MandateAmendResponse>>();
        services.AddCommand<AmendMandate, MandateAmendResponse, AmendMandateHandler>(CommandDescriptor.For("bil.Mandate.amend") with { SupportsDryRun = true });
        services.AddScoped<IValidator<CancelMandate>, CancelMandateValidator>();
        services.AddCommandAuditor<CancelMandate, MandateCancelResponse, MandateAuditor<CancelMandate, MandateCancelResponse>>();
        services.AddCommand<CancelMandate, MandateCancelResponse, CancelMandateHandler>(CommandDescriptor.For("bil.Mandate.cancel") with { SupportsDryRun = true });

        // SL2-BIL-DISB: payee accounts (IBAN P2) and the disbursement service for CLM claim payments. Both commands are
        // idempotent on the caller's key; their results carry the masked IBAN only, never the IBAN (REQ-BIL-338).
        services.AddScoped<PayeeProtection>();
        services.AddScoped<DisbursementReader>();
        services.AddScoped<TaxLevyReader>();
        services.AddScoped<IValidator<RequestLevyPayment>, RequestLevyPaymentValidator>();
        services.AddCommandAuditor<RequestLevyPayment, TaxLevyRequestLevyPaymentResponse, RequestLevyPaymentAuditor>();
        services.AddCommand<RequestLevyPayment, TaxLevyRequestLevyPaymentResponse, RequestLevyPaymentHandler>(CommandDescriptor.For("bil.TaxLevy.requestLevyPayment") with { SupportsDryRun = true });
        services.AddScoped<MigratedRecordsReader>();
        services.AddScoped<BillingOpsReader>();
        services.AddScoped<SuspenseReader>();
        services.AddScoped<BankReconciler>();
        services.AddScoped<SubLedgerInvariants>();
        // BO-BIL-12: ledger reads and the daily control totals pushed to the FIN reconciliation.
        services.AddScoped<Queries.LedgerReader>();
        services.AddScoped<FinControlTotals>();
        services.AddScoped<IBillingReconciliationService>(sp => sp.GetRequiredService<FinControlTotals>());
        services.AddSingleton<FinControlPublisher>();
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase)
            && configuration.GetValue("Billing:FinControlTotalsEnabled", true))
        {
            services.AddHostedService<FinControlSweeper>();
        }
        services.AddScoped<IValidator<ImportBankStatement>, ImportBankStatementValidator>();
        services.AddCommandAuditor<ImportBankStatement, ReconciliationBankResponse, ImportBankStatementAuditor>();
        services.AddCommand<ImportBankStatement, ReconciliationBankResponse, ImportBankStatementHandler>(
            CommandDescriptor.For("bil.Reconciliation.bank") with { SupportsDryRun = false });
        services.AddScoped<IValidator<CreatePayeeAccount>, CreatePayeeAccountValidator>();
        services.AddCommandAuditor<CreatePayeeAccount, PayeeAccountCreateResponse, CreatePayeeAccountAuditor>();
        services.AddCommand<CreatePayeeAccount, PayeeAccountCreateResponse, CreatePayeeAccountHandler>(
            CommandDescriptor.For("bil.PayeeAccount.create") with { SupportsDryRun = true });
        services.AddScoped<IValidator<VerifyPayeeAccount>, VerifyPayeeAccountValidator>();
        services.AddCommandAuditor<VerifyPayeeAccount, PayeeAccountVerifyResponse, VerifyPayeeAccountAuditor>();
        services.AddCommand<VerifyPayeeAccount, PayeeAccountVerifyResponse, VerifyPayeeAccountHandler>(
            CommandDescriptor.For("bil.PayeeAccount.verify") with { SupportsDryRun = false });
        services.AddScoped<PayeeScreener>();
        services.AddScoped<DisbursementBatchFiles>();
        services.AddScoped<IValidator<PrepareDisbursementBatch>, PrepareDisbursementBatchValidator>();
        services.AddCommandAuditor<PrepareDisbursementBatch, DisbursementBatchPrepareResponse, PrepareDisbursementBatchAuditor>();
        services.AddCommand<PrepareDisbursementBatch, DisbursementBatchPrepareResponse, PrepareDisbursementBatchHandler>(
            CommandDescriptor.For("bil.DisbursementBatch.prepare") with { SupportsDryRun = true });
        services.AddCommandAuditor<RequestDisbursementBatchRelease, DisbursementBatchReleaseResponse, RequestDisbursementBatchReleaseAuditor>();
        services.AddCommand<RequestDisbursementBatchRelease, DisbursementBatchReleaseResponse, RequestDisbursementBatchReleaseHandler>(
            CommandDescriptor.For("bil.DisbursementBatch.release") with { SupportsDryRun = true });
        services.AddCommandAuditor<ApproveDisbursementBatchRelease, DisbursementBatchApproveReleaseResponse, ApproveDisbursementBatchReleaseAuditor>();
        services.AddCommand<ApproveDisbursementBatchRelease, DisbursementBatchApproveReleaseResponse, ApproveDisbursementBatchReleaseHandler>(
            CommandDescriptor.For("bil.DisbursementBatch.approveRelease") with { SupportsDryRun = true });
        services.AddScoped<DisbursementReleaser>();
        services.AddScoped<IValidator<ApproveDisbursementRelease>, ApproveDisbursementReleaseValidator>();
        services.AddCommandAuditor<ApproveDisbursementRelease, DisbursementApproveReleaseResponse, ApproveDisbursementReleaseAuditor>();
        services.AddCommand<ApproveDisbursementRelease, DisbursementApproveReleaseResponse, ApproveDisbursementReleaseHandler>(
            CommandDescriptor.For("bil.Disbursement.approveRelease") with { SupportsDryRun = true });
        services.AddScoped<DisbursementOperator>();
        services.AddCommandAuditor<DevBankOperation, DevBankResult, DevBankOperationAuditor>();
        services.AddCommand<DevBankOperation, DevBankResult, DevBankOperationHandler>(CommandDescriptor.For("bil.DevBank.operate"));
        services.AddScoped<IValidator<StopDisbursement>, StopDisbursementValidator>();
        services.AddCommandAuditor<StopDisbursement, DisbursementStopResponse, StopDisbursementAuditor>();
        services.AddCommand<StopDisbursement, DisbursementStopResponse, StopDisbursementHandler>(
            CommandDescriptor.For("bil.Disbursement.stop") with { SupportsDryRun = true });
        services.AddScoped<IValidator<VoidDisbursement>, VoidDisbursementValidator>();
        services.AddCommandAuditor<VoidDisbursement, DisbursementVoidResponse, VoidDisbursementAuditor>();
        services.AddCommand<VoidDisbursement, DisbursementVoidResponse, VoidDisbursementHandler>(
            CommandDescriptor.For("bil.Disbursement.void") with { SupportsDryRun = true });
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

        // BO-BIL-08: the suspense workbench (unapplied cash as items): suggest (read-only), transfer (ledger move) and refund to the sender (refund path).
        services.AddScoped<IValidator<TransferSuspense>, TransferSuspenseValidator>();
        services.AddScoped<IValidator<RefundSuspenseToSender>, RefundSuspenseToSenderValidator>();
        services.AddCommandAuditor<SuggestSuspense, SuspenseSuggestResponse, SuspenseAuditor<SuggestSuspense, SuspenseSuggestResponse>>();
        services.AddCommand<SuggestSuspense, SuspenseSuggestResponse, SuggestSuspenseHandler>(CommandDescriptor.For("bil.Suspense.suggest"));
        services.AddCommandAuditor<TransferSuspense, SuspenseTransferResponse, SuspenseAuditor<TransferSuspense, SuspenseTransferResponse>>();
        services.AddCommand<TransferSuspense, SuspenseTransferResponse, TransferSuspenseHandler>(CommandDescriptor.For("bil.Suspense.transfer") with { SupportsDryRun = true });
        services.AddCommandAuditor<RefundSuspenseToSender, SuspenseRefundToSenderResponse, SuspenseAuditor<RefundSuspenseToSender, SuspenseRefundToSenderResponse>>();
        services.AddCommand<RefundSuspenseToSender, SuspenseRefundToSenderResponse, RefundSuspenseToSenderHandler>(
            CommandDescriptor.For("bil.Suspense.refundToSender") with { SupportsDryRun = true });

        // BO-BIL-06b: SEPA direct-debit collection run (pain.008 out through the bank-file port, settlement and R-transactions in).
        services.AddScoped<DirectDebitCollector>();
        services.AddSingleton<DirectDebitRunner>();
        services.AddCommandAuditor<RunDirectDebitCollection, DirectDebitRunResult, DirectDebitRunAuditor>();
        services.AddCommand<RunDirectDebitCollection, DirectDebitRunResult, RunDirectDebitCollectionHandler>(
            CommandDescriptor.For("bil.DirectDebit.run") with { RequiresIdempotencyKey = false, Idempotent = false });
        services.AddCommandAuditor<ApplyDirectDebitStatus, DirectDebitStatusResult, DirectDebitStatusAuditor>();
        services.AddCommand<ApplyDirectDebitStatus, DirectDebitStatusResult, ApplyDirectDebitStatusHandler>(
            CommandDescriptor.For("bil.DirectDebit.status") with { RequiresIdempotencyKey = false, Idempotent = false });
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase)
            && configuration.GetValue("Billing:DirectDebitEnabled", false))
        {
            services.AddHostedService<DirectDebitSweeper>();
        }

        // SL6-BIL-DELINQ: non-payment cases (notice, waiting clock, cure, cancellation request). DOC and CMP are reached through their
        // contract interfaces, resolved when used.
        services.AddScoped<NonpayService>();
        services.AddScoped<DelinquencyReader>();
        services.AddSingleton<DelinquencySweepRunner>();
        if (string.Equals(configuration["APP_ROLE"], "worker", StringComparison.OrdinalIgnoreCase)
            && configuration.GetValue("Billing:DelinquencySweepEnabled", true))
        {
            services.AddHostedService<DelinquencySweeper>();
        }

        AddInternalDelinquencyCommand<SweepDelinquency, SweepDelinquencyHandler>(services, "bil.Delinquency.sweep");
        AddInternalDelinquencyCommand<OpenDelinquency, OpenDelinquencyHandler>(services, "bil.Delinquency.open");
        AddInternalDelinquencyCommand<NoticeDelivered, NoticeDeliveredHandler>(services, "bil.Delinquency.noticeDelivered");
        AddInternalDelinquencyCommand<NoticeFailed, NoticeFailedHandler>(services, "bil.Delinquency.noticeFailed");
        AddInternalDelinquencyCommand<NonpayClockElapsed, NonpayClockElapsedHandler>(services, "bil.Delinquency.clockElapsed");
        services.AddCommandAuditor<RecordDelinquencyContact, DelinquencyRecordContactResponse, DelinquencyAuditor<RecordDelinquencyContact, DelinquencyRecordContactResponse>>();
        services.AddCommand<RecordDelinquencyContact, DelinquencyRecordContactResponse, RecordDelinquencyContactHandler>(CommandDescriptor.For("bil.Delinquency.recordContact"));
        services.AddCommandAuditor<ArrangeDelinquency, DelinquencyArrangeResponse, DelinquencyAuditor<ArrangeDelinquency, DelinquencyArrangeResponse>>();
        services.AddCommand<ArrangeDelinquency, DelinquencyArrangeResponse, ArrangeDelinquencyHandler>(CommandDescriptor.For("bil.Delinquency.arrange"));
        services.AddCommandAuditor<ReleaseDelinquencyHold, DelinquencyReleaseHoldResponse, DelinquencyAuditor<ReleaseDelinquencyHold, DelinquencyReleaseHoldResponse>>();
        services.AddCommand<ReleaseDelinquencyHold, DelinquencyReleaseHoldResponse, ReleaseDelinquencyHoldHandler>(CommandDescriptor.For("bil.Delinquency.releaseHold"));

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

        services.AddEventHandler<DocumentDeliveredV1, DocumentDeliveredNonpayHandler>(
            EventDescriptor.From(DocumentDeliveredV1.Descriptor), DocumentDeliveredNonpayHandler.Name, ModuleCode.BIL);
        services.AddEventHandler<DeliveryFailedV1, DeliveryFailedNonpayHandler>(
            EventDescriptor.From(DeliveryFailedV1.Descriptor), DeliveryFailedNonpayHandler.Name, ModuleCode.BIL);
        services.AddEventHandler<ClockElapsedV1, ClockElapsedNonpayHandler>(
            EventDescriptor.From(ClockElapsedV1.Descriptor), ClockElapsedNonpayHandler.Name, ModuleCode.BIL);

        // In-process contracts other modules call (D-ARC-16).
        services.AddScoped<IBillingBillingAccountService, BillingAccountService>();
        services.AddScoped<IBillingInvoiceService, InvoiceService>();
        services.AddScoped<IBillingPaymentService, PaymentService>();
        services.AddScoped<IBillingReceiptService, ReceiptService>();
        services.AddScoped<IBillingPayeeAccountService, PayeeAccountService>();
        services.AddScoped<IBillingDisbursementService, DisbursementService>();
        services.AddScoped<IBillingRefundService, RefundService>();
        services.AddScoped<IBillingDelinquencyService, DelinquencyService>();
        services.AddScoped<IBillingTaxLevyService, TaxLevyService>();

        services.AddErrorDefinitions(Errors);
        return services;
    }

    private static void AddInternalDelinquencyCommand<TCommand, THandler>(IServiceCollection services, string operation)
        where TCommand : Commands.IDelinquencyCommand, ICommand<int>
        where THandler : class, ICommandHandler<TCommand, int> =>
        services.AddCommand<TCommand, int, THandler>(CommandDescriptor.For(operation) with { RequiresIdempotencyKey = false, Idempotent = false });

    /// <summary>Status, bilingual title and description of every BIL-ERR code the module raises (RFC 9457, D-API-15).</summary>
    internal static ErrorDefinition[] Errors { get; } =
    [
        ErrorDefinition.For(ModuleCode.BIL, "NOT-ELIGIBLE", 422, "Δεν επιτρέπεται για αυτόν τον όρο", "Not eligible")
            .Describe("Το πρόγραμμα πληρωμής ή ο τρόπος πληρωμής δεν ισχύει για τον όρο, ή ο όρος έχει ήδη τιμολογηθεί.", "The plan or method does not apply to the term, or the term is already billed."),
        ErrorDefinition.For(ModuleCode.BIL, "MANDATE-STATE", 422, "Η εντολή άμεσης χρέωσης δεν επιτρέπει την ενέργεια", "The mandate state does not allow this")
            .Describe("Η εντολή ή το μέσο πληρωμής βρίσκεται σε κατάσταση που δεν επιτρέπει την ενέργεια (π.χ. υπογραφή μη προσχεδίου, ακυρωμένη εντολή, μέσο σε χρήση).", "The mandate or instrument is in a state that does not allow the action (for example signing a non-draft, a cancelled mandate, an instrument in use)."),
        ErrorDefinition.For(ModuleCode.BIL, "BALANCE-NOT-ZERO", 422, "Το υπόλοιπο του λογαριασμού δεν είναι μηδέν", "The account balance is not zero")
            .Describe("Ο λογαριασμός έχει ανοιχτά τιμολόγια ή μη μηδενικό υπόλοιπο και δεν κλείνει.", "The account has open invoices or a non-zero balance and cannot be closed."),
        ErrorDefinition.For(ModuleCode.BIL, "PAYER-ROLE", 422, "Ο νέος πληρωτής δεν είναι δεκτός", "The new payer is not accepted")
            .Describe("Ο νέος πληρωτής δεν υπάρχει στο μητρώο μερών, δεν είναι ενεργός ή δεν μπορεί να επαληθευτεί.", "The new payer does not exist in the party register, is not active, or cannot be verified."),
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
        ErrorDefinition.For(ModuleCode.BIL, "VOP-UNAVAILABLE", 503, "Η επαλήθευση δικαιούχου δεν είναι διαθέσιμη", "Verification of payee unavailable")
            .Describe("Η επαλήθευση δικαιούχου δεν ολοκληρώθηκε· δοκιμάστε ξανά αργότερα. Η πληρωμή δεν γίνεται χωρίς επαλήθευση.", "Verification of payee did not complete; try again later. No payment is made without it."),
        ErrorDefinition.For(ModuleCode.BIL, "NO-CREDIT", 422, "Δεν υπάρχει πιστωτικό υπόλοιπο προς επιστροφή", "No credit left to refund")
            .Describe("Ο λογαριασμός δεν έχει πιστωτικό υπόλοιπο ή αυτό συμψηφίστηκε με ανοιχτές ειδοποιήσεις πληρωμής.", "The account has no credit, or it is fully set against open invoices."),
        ErrorDefinition.For(ModuleCode.BIL, "REFUND-OPEN", 409, "Υπάρχει ήδη ανοιχτή επιστροφή", "A refund is already open")
            .Describe("Ένας λογαριασμός χρέωσης έχει μία ανοιχτή επιστροφή τη φορά· αποφασίστε πρώτα για αυτήν.", "A billing account has one open refund at a time; decide it first."),
        ErrorDefinition.For(ModuleCode.BIL, "REFUND-BELOW-MINIMUM", 422, "Η επιστροφή είναι κάτω από το ελάχιστο", "The refund is below the minimum")
            .Describe("Το ποσό προς επιστροφή είναι μικρότερο από το ελάχιστο· το πιστωτικό μένει στον λογαριασμό.", "The amount to refund is below the minimum; the credit stays on the account."),
        ErrorDefinition.For(ModuleCode.BIL, "REFUND-STATE", 409, "Η επιστροφή δεν είναι στην κατάλληλη κατάσταση", "The refund is not in the right state")
            .Describe("Η ενέργεια δεν επιτρέπεται στην τρέχουσα κατάσταση της επιστροφής.", "The action is not allowed in the refund's current state."),
        // BO-BIL-07: reversal of allocations.
        ErrorDefinition.For(ModuleCode.BIL, "NOT-ALLOCATED", 422, "Δεν υπάρχει κατανομή προς αντιλογισμό", "Nothing is allocated to reverse")
            .Describe("Η είσπραξη (ή οι κατανομές που αναφέρατε) δεν έχει πλέον ενεργή κατανομή σε τιμολόγιο.", "The receipt (or the named allocations) has no live allocation to an invoice any more."),
        ErrorDefinition.For(ModuleCode.BIL, "ALREADY-REVERSED", 409, "Η κατανομή έχει ήδη αντιλογιστεί", "The allocation was already reversed")
            .Describe("Η κατανομή αντιλογίστηκε ήδη, ή ταυτόχρονο αίτημα την αντιλόγισε πρώτο. Φορτώστε ξανά την είσπραξη.", "The allocation was reversed already, or a concurrent request reversed it first. Reload the receipt."),
        ErrorDefinition.For(ModuleCode.BIL, "DAY-NOT-CLOSED", 422, "Η λογιστική ημέρα δεν έχει ξεκινήσει", "The accounting day has not started")
            .Describe("Δεν υπάρχουν συγκεντρωτικά για μελλοντική ημέρα.", "Control totals exist for today and earlier days only."),
        ErrorDefinition.For(ModuleCode.BIL, "PERIOD-LOCKED", 422, "Η λογιστική περίοδος είναι κλειστή", "The accounting period is closed")
            .Describe("Η περίοδος της κατανομής ή της σημερινής εγγραφής έχει κλείσει στη Χρηματοοικονομική· ο αντιλογισμός δεν επιτρέπεται.", "The period of the allocation or of today's posting is closed in Finance; the reversal is refused."),
        ErrorDefinition.For(ModuleCode.BIL, "ALLOCATION-REFUNDED", 422, "Η κατανομή ανήκει σε επιστροφή που πληρώθηκε", "The allocation belongs to a refund already paid out")
            .Describe("Η ασφαλιστική περίοδος της κατανομής ανήκει σε επιστροφή που εκταμιεύεται ή έχει πληρωθεί· δεν αντιλογίζεται.", "A term of the allocation is part of a refund that is being disbursed or was paid; it cannot be reversed."),
        ErrorDefinition.For(ModuleCode.BIL, "INVOICE-STATE", 422, "Το τιμολόγιο δεν επιτρέπει αντιλογισμό", "The invoice does not allow a reversal")
            .Describe("Το τιμολόγιο είναι διαγραμμένο ή αντιλογισμένο· οι κατανομές του δεν αντιλογίζονται.", "The invoice is written off or reversed; its allocations cannot be reversed."),
        ErrorDefinition.For(ModuleCode.BIL, "WRITE-OFF-STATE", 422, "Η διαγραφή δεν είναι στην κατάλληλη κατάσταση", "The write-off is not in the right state")
            .Describe("Η διαγραφή υπολοίπου δεν βρίσκεται στην κατάσταση που απαιτεί η ενέργεια (προτεινόμενη για απόφαση, εγκεκριμένη για καταχώριση).", "The write-off is not in the state the action needs (proposed to be decided, approved to be posted)."),
        ErrorDefinition.For(ModuleCode.BIL, "PAYER-MISMATCH", 422, "Διαφορετικός πληρωτής", "Different payer")
            .Describe("Τα χρήματα μεταφέρονται μόνο μεταξύ λογαριασμών χρέωσης του ίδιου πληρωτή.", "Money moves only between billing accounts of the same payer."),
        ErrorDefinition.For(ModuleCode.BIL, "SOD", 403, "Διαχωρισμός καθηκόντων", "Segregation of duties")
            .Describe("Ο αιτών, όποιος επεξεργάστηκε την επιστροφή ή όποιος άλλαξε τον λογαριασμό δικαιούχου δεν μπορεί να την εγκρίνει.", "The requester, an editor of the refund or the person who changed the payee account cannot approve it."),
        ErrorDefinition.For(ModuleCode.BIL, "PAYEE-UNVERIFIED", 422, "Ο λογαριασμός δικαιούχου δεν έχει επαληθευτεί", "Payee account not verified")
            .Describe("Η επιστροφή πληρώνεται μόνο σε επαληθευμένο λογαριασμό του πληρωτή.", "A refund is paid only to the payer's verified bank account."),
        ErrorDefinition.For(ModuleCode.BIL, "NOT-PERMITTED", 403, "Δεν έχετε εξουσιοδότηση για αυτό το ποσό", "No authority for this amount")
            .Describe("Δεν υπάρχει εξουσιοδότηση επιστροφής για αυτό το ποσό.", "No refund authority covers this amount."),
        ErrorDefinition.For(ModuleCode.BIL, "SUSPENSE-STATE", 409, "Η εγγραφή αδιάθετου δεν είναι στην κατάλληλη κατάσταση", "The suspense item is not in the right state")
            .Describe("Η ενέργεια επιτρέπεται μόνο σε ανοιχτή εγγραφή αδιάθετης είσπραξης.", "The action is allowed only on an open suspense item."),
        ErrorDefinition.For(ModuleCode.BIL, "DELINQUENCY-STATE", 409, "Η εκκρεμότητα δεν είναι στην κατάλληλη κατάσταση", "The delinquency case is not in the right state")
            .Describe("Η ενέργεια δεν επιτρέπεται στην τρέχουσα κατάσταση της εκκρεμότητας οφειλής.", "The action is not allowed in the delinquency case's current state."),
        ErrorDefinition.For(ModuleCode.BIL, "FISCAL-TREATMENT-OPEN", 422, "Ανοικτή φορολογική μεταχείριση", "Fiscal treatment is open"),
        ErrorDefinition.For(ModuleCode.BIL, "LINES-MISMATCH", 422, "Οι γραμμές δεν αντιστοιχούν στο καθαρό ποσό", "The lines do not match the net")
            .Describe("Οι γραμμές της κατάστασης φιλικού διακανονισμού πρέπει να είναι μοναδικές και να αθροίζουν στο καθαρό ποσό.", "The Friendly Settlement statement lines must be unique and add up to the net amount."),
        ErrorDefinition.For(ModuleCode.BIL, "APPROVAL-REQUIRED", 422, "Δεν εκκρεμεί έγκριση αποδέσμευσης", "No release approval is pending")
            .Describe("Η πληρωμή δεν περιμένει έγκριση αποδέσμευσης.", "The disbursement is not awaiting a release approval."),
        ErrorDefinition.For(ModuleCode.BIL, "DUPLICATE", 409, "Πιθανή διπλή πληρωμή", "Possible duplicate payment")
            .Describe("Υπάρχει ήδη πληρωμή για την ίδια πηγή ή με τον ίδιο λογαριασμό, ποσό και αναφορά.", "A payment already exists for the same source or with the same account, amount and reference."),
        ErrorDefinition.For(ModuleCode.BIL, "LEVY-UNSETTLED", 422, "Η εισφορά δεν έχει διευθετηθεί νομικά", "The levy is not legally settled")
            .Describe("Εισφορά που στηρίζεται σε γραμμές χωρίς διευθετημένο νομικό καθεστώς δεν πληρώνεται σε παραγωγή.", "A levy that rests on rows that are not legally settled is not paid in Production."),
        ErrorDefinition.For(ModuleCode.BIL, "NOTHING-PAYABLE", 422, "Δεν υπάρχει πληρωτέα εισφορά", "There is no levy to pay")
            .Describe("Δεν υπάρχει θετικό καθαρό ποσό εισφοράς για την περίοδο και το νόμισμα.", "There is no positive net levy for the period and currency."),
        ErrorDefinition.For(ModuleCode.BIL, "HELD-ITEMS", 422, "Πληρωμές σε αναμονή", "Disbursements on hold")
            .Describe("Μία ή περισσότερες πληρωμές δεν μπορούν να μπουν σε δέσμη (δεν είναι εγκεκριμένες, είναι ήδη σε δέσμη ή ο δικαιούχος δεν είναι επαληθευμένος).", "One or more disbursements cannot enter a batch (not Approved, already in a batch, or the payee is not verified)."),
        ErrorDefinition.For(ModuleCode.BIL, "STATEMENT-INVALID", 422, "Μη έγκυρο αντίγραφο κίνησης λογαριασμού", "The bank statement is not valid")
            .Describe("Το αρχείο δεν είναι έγκυρο camt.053 ή CSV, ή αφορά άλλον τραπεζικό λογαριασμό· δεν εισάγεται τίποτα.", "The file is not a well-formed camt.053 or CSV statement of the named bank account; nothing is imported."),
        ErrorDefinition.For(ModuleCode.BIL, "STATEMENT-CONFLICT", 409, "Σύγκρουση αντιγράφου κίνησης", "Bank statement conflict")
            .Describe("Αντίγραφο με την ίδια αναφορά έχει ήδη εισαχθεί με διαφορετικό περιεχόμενο.", "A statement with this reference was imported before with different content."),
        ErrorDefinition.For(ModuleCode.BIL, "METHOD-UNAVAILABLE", 503, "Η τράπεζα δεν είναι διαθέσιμη", "Bank channel unavailable")
            .Describe("Δεν έχει συνδεθεί τραπεζικό κανάλι ή αποθήκη αρχείων τράπεζας· η πληρωμή δεν γίνεται (fail closed).", "No bank channel or bank-file store is bound; nothing is prepared or released (fail closed)."),
    ];
}

