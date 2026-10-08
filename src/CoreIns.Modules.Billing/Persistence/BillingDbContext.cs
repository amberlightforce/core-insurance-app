using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Billing.Persistence;

/// <summary>
/// The Billing module's EF Core context (schema <c>bil</c>). It shares the scope's connection and transaction through
/// <see cref="DbSession"/>, so billing rows, ledger entries, outbox events and audit records commit together
/// (REQ-BIL-332). The sub-ledger (<c>ledger_entry</c>, <c>ledger_line</c>) and <c>allocation</c> are append-only: the
/// app role has no UPDATE/DELETE on them and triggers refuse UPDATE, DELETE and TRUNCATE for every role (REQ-BIL-281);
/// a deferred constraint trigger refuses an unbalanced entry at commit (REQ-BIL-280). Those triggers, the charge freeze
/// and the seeded chart and rules live in the migration.
/// </summary>
internal sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : ModuleDbContext(options)
{
    public DbSet<BillingAccountRow> Accounts => Set<BillingAccountRow>();

    public DbSet<PlanInstanceRow> PlanInstances => Set<PlanInstanceRow>();

    public DbSet<ChargeRow> Charges => Set<ChargeRow>();

    public DbSet<InvoiceRow> Invoices => Set<InvoiceRow>();

    public DbSet<InvoiceItemRow> InvoiceItems => Set<InvoiceItemRow>();

    public DbSet<ReceiptRow> Receipts => Set<ReceiptRow>();

    public DbSet<AllocationRow> Allocations => Set<AllocationRow>();

    public DbSet<LedgerAccountRow> LedgerAccounts => Set<LedgerAccountRow>();

    public DbSet<LedgerRuleRow> LedgerRules => Set<LedgerRuleRow>();

    public DbSet<LedgerEntryRow> LedgerEntries => Set<LedgerEntryRow>();

    public DbSet<LedgerLineRow> LedgerLines => Set<LedgerLineRow>();

    public DbSet<IntakeExceptionRow> IntakeExceptions => Set<IntakeExceptionRow>();

    public DbSet<PayeeAccountRow> PayeeAccounts => Set<PayeeAccountRow>();

    public DbSet<DisbursementRow> Disbursements => Set<DisbursementRow>();

    protected override string Schema => BillingModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<BillingAccountRow>(entity =>
        {
            entity.ToTable("billing_account", table =>
            {
                table.HasCheckConstraint("ck_billing_account_status", Codes.CheckSql<BillingAccountStatus>("status"));
                table.HasCheckConstraint("ck_billing_account_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("ck_billing_account_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_billing_account_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.BillingAccountId).HasName("pk_billing_account");
            entity.Property(e => e.BillingAccountId).HasColumnName("billing_account_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.AccountNumber).HasColumnName("account_number");
            entity.Property(e => e.PayerPartyId).HasColumnName("payer_party_id");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.AccountNumber }).IsUnique().HasDatabaseName("ux_billing_account_number");

            // REQ-BIL-033: one Active account per payer, legal entity and currency (the slice creates no standalone accounts).
            entity.HasIndex(e => new { e.LegalEntityId, e.PayerPartyId, e.Currency }).IsUnique()
                .HasFilter("status = 'ACTIVE'").HasDatabaseName("ux_billing_account_active_payer");
        });

        modelBuilder.Entity<PlanInstanceRow>(entity =>
        {
            entity.ToTable("plan_instance", table =>
            {
                table.HasCheckConstraint("ck_plan_instance_term", "term_to > term_from");
                table.HasCheckConstraint("ck_plan_instance_bill_mode", "bill_mode IN ('DIRECT_BILL', 'AGENCY_BILL')");
            });
            entity.HasKey(e => e.TermId).HasName("pk_plan_instance");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.PlanInstanceId).HasColumnName("plan_instance_id");
            entity.Property(e => e.BillingAccountId).HasColumnName("billing_account_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.PolicyNumber).HasColumnName("policy_number");
            entity.Property(e => e.TermNumber).HasColumnName("term_number");
            entity.Property(e => e.BoundTransactionId).HasColumnName("bound_transaction_id");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.ProductVersion).HasColumnName("product_version");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash");
            entity.Property(e => e.PlanCode).HasColumnName("plan_code");
            entity.Property(e => e.BillMode).HasColumnName("bill_mode");
            entity.Property(e => e.Method).HasColumnName("method");
            entity.Property(e => e.TermFrom).HasColumnName("term_from");
            entity.Property(e => e.TermTo).HasColumnName("term_to");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.Property(e => e.SourceSequence).HasColumnName("source_sequence");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.PlanInstanceId).IsUnique().HasDatabaseName("ux_plan_instance_id");
            entity.HasIndex(e => e.BillingAccountId).HasDatabaseName("ix_plan_instance_account");
            entity.HasIndex(e => e.PolicyId).HasDatabaseName("ix_plan_instance_policy");
            entity.HasOne<BillingAccountRow>().WithMany().HasForeignKey(e => e.BillingAccountId)
                .HasConstraintName("fk_plan_instance_account").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ChargeRow>(entity =>
        {
            entity.ToTable("charge", table =>
            {
                table.HasCheckConstraint("ck_charge_set", "set_index >= 1 AND set_index <= set_size");
                table.HasCheckConstraint("ck_charge_status", Codes.CheckSql<ChargeStatus>("status"));
                table.HasCheckConstraint("ck_charge_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_charge_valid", "valid_to IS NULL OR valid_to > valid_from");
                table.HasCheckConstraint("ck_charge_quarantine", "(status = 'QUARANTINED') = (quarantine_reason IS NOT NULL)");
            });
            entity.HasKey(e => e.ChargeId).HasName("pk_charge");
            entity.Property(e => e.ChargeId).HasColumnName("charge_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.SetId).HasColumnName("set_id");
            entity.Property(e => e.SetSize).HasColumnName("set_size");
            entity.Property(e => e.SetIndex).HasColumnName("set_index");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.ElementLocator).HasColumnName("element_locator");
            entity.Property(e => e.CoverageCode).HasColumnName("coverage_code");
            entity.Property(e => e.ChargeType).HasColumnName("charge_type");
            entity.Property(e => e.ChargeCategory).HasColumnName("charge_category");
            entity.Property(e => e.DeltaKind).HasColumnName("delta_kind");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.BookingDate).HasColumnName("booking_date");
            entity.Property(e => e.CorrelationKey).HasColumnName("correlation_key");
            entity.Property(e => e.TaxTreatmentRef).HasColumnName("tax_treatment_ref");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.Property(e => e.SourceSequence).HasColumnName("source_sequence");
            entity.Property(e => e.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.QuarantineReason).HasColumnName("quarantine_reason");
            entity.Property(e => e.FiscalCategoryKey).HasColumnName("fiscal_category_key");
            entity.Property(e => e.LegalStatus).HasColumnName("legal_status");
            entity.Property(e => e.Provisional).HasColumnName("provisional").HasDefaultValue(false);
            entity.Property(e => e.WrittenEntryId).HasColumnName("written_entry_id");
            entity.HasIndex(e => new { e.SetId, e.SetIndex }).IsUnique().HasDatabaseName("ux_charge_set_member");
            entity.HasIndex(e => e.TermId).HasDatabaseName("ix_charge_term");
        });

        modelBuilder.Entity<InvoiceRow>(entity =>
        {
            entity.ToTable("invoice", table =>
            {
                table.HasCheckConstraint("ck_invoice_state", Codes.CheckSql<InvoiceState>("state"));
                table.HasCheckConstraint("ck_invoice_kind", "kind IN ('INVOICE', 'CREDIT_NOTE')");
                table.HasCheckConstraint("ck_invoice_fiscal_status", Codes.CheckSql<FiscalStatus>("fiscal_status"));
                table.HasCheckConstraint("ck_invoice_total", "total > 0");
                table.HasCheckConstraint("ck_invoice_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_invoice_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.InvoiceId).HasName("pk_invoice");
            entity.Property(e => e.InvoiceId).HasColumnName("invoice_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.BillingAccountId).HasColumnName("billing_account_id");
            entity.Property(e => e.InvoiceNumber).HasColumnName("invoice_number");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.IssueDate).HasColumnName("issue_date");
            entity.Property(e => e.DueDate).HasColumnName("due_date");
            entity.Property(e => e.Method).HasColumnName("method");
            entity.Property(e => e.Total).HasColumnName("total").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.FiscalStatus).HasColumnName("fiscal_status");
            entity.Property(e => e.FiscalTriggerPoint).HasColumnName("fiscal_trigger_point");
            entity.Property(e => e.FiscalDocumentId).HasColumnName("fiscal_document_id");
            entity.Property(e => e.FiscalDocumentType).HasColumnName("fiscal_document_type");
            entity.Property(e => e.FiscalSeries).HasColumnName("fiscal_series");
            entity.Property(e => e.FiscalNumber).HasColumnName("fiscal_number");
            entity.Property(e => e.FiscalMark).HasColumnName("fiscal_mark");
            entity.Property(e => e.FiscalUid).HasColumnName("fiscal_uid");
            entity.Property(e => e.FiscalRejectionCodes).HasColumnName("fiscal_rejection_codes");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.InvoiceNumber }).IsUnique().HasDatabaseName("ux_invoice_number");

            // One invoice per bound transaction in the ANNUAL slice: a replayed set can never bill twice.
            entity.HasIndex(e => new { e.TransactionId, e.Kind }).IsUnique().HasDatabaseName("ux_invoice_transaction");
            entity.HasIndex(e => e.BillingAccountId).HasDatabaseName("ix_invoice_account");
            entity.HasIndex(e => e.PolicyId).HasDatabaseName("ix_invoice_policy");
            entity.HasIndex(e => e.FiscalDocumentId).HasDatabaseName("ix_invoice_fiscal_document");
            entity.HasOne<BillingAccountRow>().WithMany().HasForeignKey(e => e.BillingAccountId)
                .HasConstraintName("fk_invoice_account").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InvoiceItemRow>(entity =>
        {
            entity.ToTable("invoice_item", table =>
            {
                table.HasCheckConstraint("ck_invoice_item_state", Codes.CheckSql<InvoiceItemState>("state"));
                table.HasCheckConstraint("ck_invoice_item_amount", "amount > 0");
                table.HasCheckConstraint("ck_invoice_item_currency", "currency ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(e => e.InvoiceItemId).HasName("pk_invoice_item");
            entity.Property(e => e.InvoiceItemId).HasColumnName("invoice_item_id");
            entity.Property(e => e.InvoiceId).HasColumnName("invoice_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ChargeId).HasColumnName("charge_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.ElementLocator).HasColumnName("element_locator");
            entity.Property(e => e.CoverageCode).HasColumnName("coverage_code");
            entity.Property(e => e.ChargeType).HasColumnName("charge_type");
            entity.Property(e => e.ChargeCategory).HasColumnName("charge_category");
            entity.Property(e => e.FiscalCategoryKey).HasColumnName("fiscal_category_key");
            entity.Property(e => e.LegalStatus).HasColumnName("legal_status");
            entity.Property(e => e.Provisional).HasColumnName("provisional").HasDefaultValue(false);
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.LineNo).HasColumnName("line_no");

            // An ANNUAL charge is billed exactly once (REQ-BIL-002: one set of scheduled items).
            entity.HasIndex(e => e.ChargeId).IsUnique().HasDatabaseName("ux_invoice_item_charge");
            entity.HasIndex(e => e.InvoiceId).HasDatabaseName("ix_invoice_item_invoice");
            entity.HasOne<InvoiceRow>().WithMany().HasForeignKey(e => e.InvoiceId)
                .HasConstraintName("fk_invoice_item_invoice").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ChargeRow>().WithMany().HasForeignKey(e => e.ChargeId)
                .HasConstraintName("fk_invoice_item_charge").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReceiptRow>(entity =>
        {
            entity.ToTable("receipt", table =>
            {
                table.HasCheckConstraint("ck_receipt_state", Codes.CheckSql<PaymentState>("state"));
                table.HasCheckConstraint("ck_receipt_amount", "amount > 0");
                table.HasCheckConstraint("ck_receipt_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_receipt_method", "method IN ('BANK_TRANSFER', 'CASHIER')");
                table.HasCheckConstraint("ck_receipt_suspense", "(state = 'SUSPENSE') = (suspense_reason IS NOT NULL)");
                table.HasCheckConstraint("ck_receipt_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.ReceiptId).HasName("pk_receipt");
            entity.Property(e => e.ReceiptId).HasColumnName("receipt_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.BillingAccountId).HasColumnName("billing_account_id");
            entity.Property(e => e.ReceiptNumber).HasColumnName("receipt_number");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.Method).HasColumnName("method");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.ValueDate).HasColumnName("value_date");
            entity.Property(e => e.AccountingDate).HasColumnName("accounting_date");
            entity.Property(e => e.InvoiceRef).HasColumnName("invoice_ref");
            entity.Property(e => e.BankReference).HasColumnName("bank_reference");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.SuspenseReason).HasColumnName("suspense_reason");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.ReceiptNumber }).IsUnique().HasDatabaseName("ux_receipt_number");
            entity.HasIndex(e => e.BillingAccountId).HasDatabaseName("ix_receipt_account");
            entity.HasOne<BillingAccountRow>().WithMany().HasForeignKey(e => e.BillingAccountId)
                .HasConstraintName("fk_receipt_account").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AllocationRow>(entity =>
        {
            entity.ToTable("allocation", table =>
            {
                table.HasCheckConstraint("ck_allocation_amount", "amount > 0");
                table.HasCheckConstraint("ck_allocation_source", "source IN ('RULE', 'AI', 'MANUAL')");
                table.HasCheckConstraint("ck_allocation_currency", "currency ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(e => e.AllocationId).HasName("pk_allocation");
            entity.Property(e => e.AllocationId).HasColumnName("allocation_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ReceiptId).HasColumnName("receipt_id");
            entity.Property(e => e.InvoiceId).HasColumnName("invoice_id");
            entity.Property(e => e.InvoiceItemId).HasColumnName("invoice_item_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.RuleId).HasColumnName("rule_id");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.Actor).HasColumnName("actor");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.ReceiptId).HasDatabaseName("ix_allocation_receipt");
            entity.HasIndex(e => e.InvoiceItemId).HasDatabaseName("ix_allocation_item");
            entity.HasOne<ReceiptRow>().WithMany().HasForeignKey(e => e.ReceiptId)
                .HasConstraintName("fk_allocation_receipt").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<InvoiceItemRow>().WithMany().HasForeignKey(e => e.InvoiceItemId)
                .HasConstraintName("fk_allocation_item").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerAccountRow>(entity =>
        {
            entity.ToTable("ledger_account", table =>
            {
                table.HasCheckConstraint("ck_ledger_account_code", "account_code ~ '^LA-[0-9]{2,3}$'");
                table.HasCheckConstraint("ck_ledger_account_normal", "normal_balance IN ('DEBIT', 'CREDIT', 'EITHER')");
            });
            entity.HasKey(e => e.AccountCode).HasName("pk_ledger_account");
            entity.Property(e => e.AccountCode).HasColumnName("account_code");
            entity.Property(e => e.NameEn).HasColumnName("name_en");
            entity.Property(e => e.NameEl).HasColumnName("name_el");
            entity.Property(e => e.AccountType).HasColumnName("account_type");
            entity.Property(e => e.NormalBalance).HasColumnName("normal_balance");
        });

        modelBuilder.Entity<LedgerRuleRow>(entity =>
        {
            entity.ToTable("ledger_rule", table => table.HasCheckConstraint("ck_ledger_rule_version", "version >= 1"));
            entity.HasKey(e => new { e.RuleId, e.Version }).HasName("pk_ledger_rule");
            entity.Property(e => e.RuleId).HasColumnName("rule_id");
            entity.Property(e => e.Version).HasColumnName("version");
            entity.Property(e => e.EventType).HasColumnName("event_type");
            entity.Property(e => e.ChargeCategory).HasColumnName("charge_category");
            entity.Property(e => e.BillMode).HasColumnName("bill_mode");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction");
            entity.Property(e => e.Qualifier).HasColumnName("qualifier");
            entity.Property(e => e.DebitAccount).HasColumnName("debit_account");
            entity.Property(e => e.CreditAccount).HasColumnName("credit_account");
            entity.Property(e => e.AmountExpression).HasColumnName("amount_expression");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.HasOne<LedgerAccountRow>().WithMany().HasForeignKey(e => e.DebitAccount)
                .HasConstraintName("fk_ledger_rule_debit").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LedgerAccountRow>().WithMany().HasForeignKey(e => e.CreditAccount)
                .HasConstraintName("fk_ledger_rule_credit").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerEntryRow>(entity =>
        {
            entity.ToTable("ledger_entry", table =>
            {
                table.HasCheckConstraint("ck_ledger_entry_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");

                // An entry belongs to exactly one aggregate: a billing account or a disbursement (SL2-BIL-DISB).
                table.HasCheckConstraint("ck_ledger_entry_owner", "(billing_account_id IS NULL) <> (disbursement_id IS NULL)");
            });
            entity.HasKey(e => e.EntryId).HasName("pk_ledger_entry");
            entity.Property(e => e.EntryId).HasColumnName("entry_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.BillingAccountId).HasColumnName("billing_account_id");
            entity.Property(e => e.EntryType).HasColumnName("entry_type");
            entity.Property(e => e.AccountingDate).HasColumnName("accounting_date");
            entity.Property(e => e.BusinessDate).HasColumnName("business_date");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.Property(e => e.CauseEventId).HasColumnName("cause_event_id");
            entity.Property(e => e.CauseOperation).HasColumnName("cause_operation");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.LineageKeys).HasColumnName("lineage_keys").HasColumnType("jsonb");
            entity.Property(e => e.ReversesEntryId).HasColumnName("reverses_entry_id");
            entity.Property(e => e.DisbursementId).HasColumnName("disbursement_id");
            entity.HasIndex(e => new { e.LegalEntityId, e.AccountingDate }).HasDatabaseName("ix_ledger_entry_date");
            entity.HasIndex(e => e.BillingAccountId).HasDatabaseName("ix_ledger_entry_account");
            entity.HasIndex(e => e.DisbursementId).HasDatabaseName("ix_ledger_entry_disbursement");
        });

        modelBuilder.Entity<LedgerLineRow>(entity =>
        {
            entity.ToTable("ledger_line", table =>
            {
                table.HasCheckConstraint("ck_ledger_line_side", "side IN ('DEBIT', 'CREDIT')");
                table.HasCheckConstraint("ck_ledger_line_amount", "amount > 0");
                table.HasCheckConstraint("ck_ledger_line_currency", "currency ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(e => e.LineId).HasName("pk_ledger_line");
            entity.Property(e => e.LineId).HasColumnName("line_id");
            entity.Property(e => e.EntryId).HasColumnName("entry_id");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.AccountCode).HasColumnName("account_code");
            entity.Property(e => e.Side).HasColumnName("side");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.RuleId).HasColumnName("rule_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.BillingAccountId).HasColumnName("billing_account_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.ChargeId).HasColumnName("charge_id");
            entity.Property(e => e.ChargeType).HasColumnName("charge_type");
            entity.Property(e => e.ChargeCategory).HasColumnName("charge_category");
            entity.Property(e => e.CoverageCode).HasColumnName("coverage_code");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.BillMode).HasColumnName("bill_mode");
            entity.Property(e => e.InvoiceId).HasColumnName("invoice_id");
            entity.Property(e => e.InvoiceItemId).HasColumnName("invoice_item_id");
            entity.Property(e => e.ReceiptId).HasColumnName("receipt_id");
            entity.Property(e => e.AllocationId).HasColumnName("allocation_id");
            entity.Property(e => e.DisbursementId).HasColumnName("disbursement_id");
            entity.Property(e => e.SourceType).HasColumnName("source_type");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.HasIndex(e => new { e.EntryId, e.LineNo }).IsUnique().HasDatabaseName("ux_ledger_line_no");
            entity.HasIndex(e => new { e.BillingAccountId, e.AccountCode }).HasDatabaseName("ix_ledger_line_account");
            entity.HasOne<LedgerEntryRow>().WithMany().HasForeignKey(e => e.EntryId)
                .HasConstraintName("fk_ledger_line_entry").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LedgerAccountRow>().WithMany().HasForeignKey(e => e.AccountCode)
                .HasConstraintName("fk_ledger_line_account").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntakeExceptionRow>(entity =>
        {
            entity.ToTable("intake_exception");
            entity.HasKey(e => e.ExceptionId).HasName("pk_intake_exception");
            entity.Property(e => e.ExceptionId).HasColumnName("exception_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.Subject).HasColumnName("subject");
            entity.Property(e => e.ReasonCode).HasColumnName("reason_code");
            entity.Property(e => e.Detail).HasColumnName("detail");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.Property(e => e.RaisedAt).HasColumnName("raised_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.Kind, e.Subject, e.ReasonCode }).IsUnique().HasDatabaseName("ux_intake_exception");
        });

        ConfigurePayeeAccounts(modelBuilder);
        ConfigureDisbursements(modelBuilder);
    }

    /// <summary>Payee accounts (REQ-BIL-343): IBAN as ciphertext, blind index and last four characters only.</summary>
    private static void ConfigurePayeeAccounts(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<PayeeAccountRow>(entity =>
        {
            entity.ToTable("payee_account", table =>
            {
                table.HasCheckConstraint("ck_payee_account_status", Codes.CheckSql<PayeeAccountStatus>("status"));
                table.HasCheckConstraint("ck_payee_account_verification", Codes.CheckSql<PayeeVerification>("verification_status"));
                table.HasCheckConstraint("ck_payee_account_vop", "vop_result IS NULL OR " + Codes.CheckSql<VopOutcome>("vop_result"));
                table.HasCheckConstraint("ck_payee_account_purpose", "purpose ~ '^[A-Z][A-Z_]{1,31}$'");
                table.HasCheckConstraint("ck_payee_account_last4", "iban_last4 ~ '^[A-Z0-9]{4}$'");
                table.HasCheckConstraint("ck_payee_account_valid", "valid_to IS NULL OR valid_to >= valid_from");
                table.HasCheckConstraint("ck_payee_account_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.PayeeAccountId).HasName("pk_payee_account");
            entity.Property(e => e.PayeeAccountId).HasColumnName("payee_account_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.Purpose).HasColumnName("purpose");
            entity.Property(e => e.IbanEncrypted).HasColumnName("iban_encrypted");
            entity.Property(e => e.IbanBlindIndex).HasColumnName("iban_blind_index");
            entity.Property(e => e.IbanLast4).HasColumnName("iban_last4").HasColumnType("char(4)");
            entity.Property(e => e.HolderName).HasColumnName("holder_name");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.EvidenceRef).HasColumnName("evidence_ref");
            entity.Property(e => e.VerificationStatus).HasColumnName("verification_status");
            entity.Property(e => e.VopResult).HasColumnName("vop_result");
            entity.Property(e => e.VopSuggestedName).HasColumnName("vop_suggested_name");
            entity.Property(e => e.VopCheckedAt).HasColumnName("vop_checked_at").HasColumnType("timestamptz");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.CoolingOffUntil).HasColumnName("cooling_off_until");
            entity.Property(e => e.IsChange).HasColumnName("is_change");
            entity.Property(e => e.SupersedesId).HasColumnName("supersedes_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.PartyId, e.Purpose }).HasDatabaseName("ix_payee_account_party");
            entity.HasIndex(e => e.IbanBlindIndex).HasDatabaseName("ix_payee_account_iban_index");

            // One Active account per party and purpose: a change supersedes the previous one (REQ-BIL-343).
            entity.HasIndex(e => new { e.LegalEntityId, e.PartyId, e.Purpose }).IsUnique()
                .HasFilter("status = 'ACTIVE'").HasDatabaseName("ux_payee_account_active");
        });

    /// <summary>Disbursements (REQ-BIL-009): duplicate key (payee account, amount, source reference = the claim) and one live disbursement per source object.</summary>
    private static void ConfigureDisbursements(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<DisbursementRow>(entity =>
        {
            entity.ToTable("disbursement", table =>
            {
                table.HasCheckConstraint("ck_disbursement_state", Codes.CheckSql<DisbursementState>("state"));
                table.HasCheckConstraint("ck_disbursement_amount", "amount > 0");
                table.HasCheckConstraint("ck_disbursement_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_disbursement_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("ck_disbursement_hash", "approval_content_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_disbursement_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.DisbursementId).HasName("pk_disbursement");
            entity.Property(e => e.DisbursementId).HasColumnName("disbursement_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.DisbursementNumber).HasColumnName("disbursement_number");
            entity.Property(e => e.SourceModule).HasColumnName("source_module");
            entity.Property(e => e.SourceType).HasColumnName("source_type");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.PayeePartyId).HasColumnName("payee_party_id");
            entity.Property(e => e.PayeeAccountId).HasColumnName("payee_account_id");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Method).HasColumnName("method");
            entity.Property(e => e.RequestedValueDate).HasColumnName("requested_value_date");
            entity.Property(e => e.ValueDate).HasColumnName("value_date");
            entity.Property(e => e.ApprovalEvidenceRef).HasColumnName("approval_evidence_ref");
            entity.Property(e => e.ApprovalContentHash).HasColumnName("approval_content_hash").HasColumnType("char(64)");
            entity.Property(e => e.PurposeText).HasColumnName("purpose_text");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.ScreeningResult).HasColumnName("screening_result");
            entity.Property(e => e.ScreeningListVersions).HasColumnName("screening_list_versions");
            entity.Property(e => e.ScreenedAt).HasColumnName("screened_at").HasColumnType("timestamptz");
            entity.Property(e => e.VopResult).HasColumnName("vop_result");
            entity.Property(e => e.BankReference).HasColumnName("bank_reference");
            entity.Property(e => e.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamptz");
            entity.Property(e => e.ReleasedAt).HasColumnName("released_at").HasColumnType("timestamptz");
            entity.Property(e => e.IssuedAt).HasColumnName("issued_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClearedAt).HasColumnName("cleared_at").HasColumnType("timestamptz");
            entity.Property(e => e.ReleaseEntryId).HasColumnName("release_entry_id");
            entity.Property(e => e.ClearEntryId).HasColumnName("clear_entry_id");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.DisbursementNumber }).IsUnique().HasDatabaseName("ux_disbursement_number");
            entity.HasIndex(e => e.ClaimId).HasDatabaseName("ix_disbursement_claim");

            // REQ-BIL-202, two independent keys; a Rejected, Stopped, Voided or Returned disbursement no longer blocks:
            // - the duplicate key: payee account, amount and source reference, where the source reference of a CLM claim
            //   payment is the claim (two payment ids on one claim to the same account for the same amount are a duplicate;
            //   a legitimate equal repeat payment needs an override, not built yet);
            // - one live disbursement per source object (the same claim payment id requested twice).
            const string live = "state NOT IN ('REJECTED', 'STOPPED', 'VOIDED', 'RETURNED')";
            entity.HasIndex(e => new { e.LegalEntityId, e.PayeeAccountId, e.Amount, e.Currency, e.SourceType, e.ClaimId }).IsUnique()
                .HasFilter(live + " AND claim_id IS NOT NULL").HasDatabaseName("ux_disbursement_duplicate_key");
            entity.HasIndex(e => new { e.LegalEntityId, e.SourceType, e.SourceId }).IsUnique()
                .HasFilter(live).HasDatabaseName("ux_disbursement_source");
            entity.HasOne<PayeeAccountRow>().WithMany().HasForeignKey(e => e.PayeeAccountId)
                .HasConstraintName("fk_disbursement_payee_account").OnDelete(DeleteBehavior.Restrict);
        });
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Billing</c> (no connection opened).</summary>
internal sealed class BillingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<BillingDbContext>("Host=localhost;Database=coreins_design;Username=design", BillingModule.Schema));
}
