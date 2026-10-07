using CoreIns.Modules.Finance.Domain;
using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Finance.Persistence;

/// <summary>
/// The Finance module's EF Core context (schema <c>fin</c>) on the scope's connection and transaction. Writes go through
/// this context; reads use Dapper (<see cref="Queries.JournalReader"/>). Triggers, the deferred balance constraint and the
/// seed are added by the migrations as SQL (<see cref="FinanceSql"/>). Internal by architecture rule.
/// </summary>
internal sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : ModuleDbContext(options)
{
    public DbSet<BookProfileRow> BookProfiles => Set<BookProfileRow>();

    public DbSet<GlAccountRow> Accounts => Set<GlAccountRow>();

    public DbSet<AccountDerivationRow> Derivations => Set<AccountDerivationRow>();

    public DbSet<EventCatalogueRow> Catalogue => Set<EventCatalogueRow>();

    public DbSet<PostingRuleSetRow> RuleSets => Set<PostingRuleSetRow>();

    public DbSet<PostingRuleRow> Rules => Set<PostingRuleRow>();

    public DbSet<ChargeTypeViewRow> ChargeTypes => Set<ChargeTypeViewRow>();

    public DbSet<PolicyContextRow> PolicyContexts => Set<PolicyContextRow>();

    public DbSet<BusinessEventRow> BusinessEvents => Set<BusinessEventRow>();

    public DbSet<FinancialPeriodRow> Periods => Set<FinancialPeriodRow>();

    public DbSet<JournalEntryRow> Journals => Set<JournalEntryRow>();

    public DbSet<JournalLineRow> JournalLines => Set<JournalLineRow>();

    protected override string Schema => FinanceModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ReferenceData(modelBuilder);
        Intake(modelBuilder);
        JournalTables(modelBuilder);
    }

    private static string In(string column, IEnumerable<string> values) => $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";

    private static void ReferenceData(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BookProfileRow>(entity =>
        {
            entity.ToTable("book_profile", t => t.HasCheckConstraint("ck_book_profile_currency", "functional_currency ~ '^[A-Z]{3}$'"));
            entity.HasKey(e => e.LegalEntityCode).HasName("pk_book_profile");
            entity.Property(e => e.LegalEntityCode).HasColumnName("legal_entity_code");
            entity.Property(e => e.FunctionalCurrency).HasColumnName("functional_currency").HasColumnType("char(3)");
            entity.Property(e => e.StatutoryBook).HasColumnName("statutory_book");
            entity.Property(e => e.ActiveBooks).HasColumnName("active_books");
            entity.Property(e => e.Source).HasColumnName("source");
        });

        modelBuilder.Entity<GlAccountRow>(entity =>
        {
            entity.ToTable("gl_account", t =>
            {
                t.HasCheckConstraint("ck_gl_account_type", In("account_type", ["ASSET", "LIABILITY", "EQUITY", "INCOME", "EXPENSE", "MEMO", "CLEARING"]));
                t.HasCheckConstraint("ck_gl_account_normal_balance", In("normal_balance", [Sides.Debit, Sides.Credit]));
                t.HasCheckConstraint("ck_gl_account_origin", In("code_origin", ["PRD09_ILLUSTRATIVE", "TECHNICAL_PLACEHOLDER"]));
                t.HasCheckConstraint("ck_gl_account_status", In("status", ["ACTIVE", "RETIRED"]));
            });
            entity.HasKey(e => new { e.LegalEntityCode, e.Book, e.AccountCode }).HasName("pk_gl_account");
            entity.Property(e => e.LegalEntityCode).HasColumnName("legal_entity_code");
            entity.Property(e => e.Book).HasColumnName("book");
            entity.Property(e => e.AccountCode).HasColumnName("account_code");
            entity.Property(e => e.NameEl).HasColumnName("name_el");
            entity.Property(e => e.NameEn).HasColumnName("name_en");
            entity.Property(e => e.AccountType).HasColumnName("account_type");
            entity.Property(e => e.NormalBalance).HasColumnName("normal_balance");
            entity.Property(e => e.CodeOrigin).HasColumnName("code_origin");
            entity.Property(e => e.SystemOnly).HasColumnName("system_only");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Source).HasColumnName("source");
        });

        modelBuilder.Entity<AccountDerivationRow>(entity =>
        {
            entity.ToTable("account_derivation", t => t.HasCheckConstraint("ck_account_derivation_period", "valid_to IS NULL OR valid_to > valid_from"));
            entity.HasKey(e => new { e.LegalEntityCode, e.Book, e.GlKey, e.ValidFrom }).HasName("pk_account_derivation");
            entity.Property(e => e.LegalEntityCode).HasColumnName("legal_entity_code");
            entity.Property(e => e.Book).HasColumnName("book");
            entity.Property(e => e.GlKey).HasColumnName("gl_key");
            entity.Property(e => e.AccountCode).HasColumnName("account_code");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.HasOne<GlAccountRow>().WithMany().HasForeignKey(e => new { e.LegalEntityCode, e.Book, e.AccountCode })
                .HasConstraintName("fk_account_derivation_account").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EventCatalogueRow>(entity =>
        {
            entity.ToTable("event_catalogue", t => t.HasCheckConstraint("ck_event_catalogue_relevance", In("relevance", Relevance.All)));
            entity.HasKey(e => new { e.RegistryName, e.SchemaMajor }).HasName("pk_event_catalogue");
            entity.Property(e => e.RegistryName).HasColumnName("registry_name");
            entity.Property(e => e.SchemaMajor).HasColumnName("schema_major");
            entity.Property(e => e.Relevance).HasColumnName("relevance");
            entity.Property(e => e.AmountFields).HasColumnName("amount_fields");
            entity.Property(e => e.Note).HasColumnName("note");
        });

        modelBuilder.Entity<PostingRuleSetRow>(entity =>
        {
            entity.ToTable("posting_rule_set", t =>
            {
                t.HasCheckConstraint("ck_posting_rule_set_status", In("status", [RuleSetStatus.Active, RuleSetStatus.Superseded]));
                t.HasCheckConstraint("ck_posting_rule_set_version", "version_no >= 1");
                t.HasCheckConstraint("ck_posting_rule_set_hash", "content_hash ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(e => e.RuleSetId).HasName("pk_posting_rule_set");
            entity.Property(e => e.RuleSetId).HasColumnName("rule_set_id");
            entity.Property(e => e.LegalEntityCode).HasColumnName("legal_entity_code");
            entity.Property(e => e.Book).HasColumnName("book");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityCode, e.Book, e.VersionNo }).IsUnique().HasDatabaseName("ux_posting_rule_set_version");
        });

        modelBuilder.Entity<PostingRuleRow>(entity =>
        {
            entity.ToTable("posting_rule", t =>
            {
                t.HasCheckConstraint("ck_posting_rule_target", "(account_code IS NULL) <> (derive_from IS NULL)");
                t.HasCheckConstraint("ck_posting_rule_derive", "derive_from IS NULL OR derive_from = 'GL_KEY'");
                t.HasCheckConstraint("ck_posting_rule_specificity",
                    "specificity = (CASE WHEN charge_category IS NULL THEN 0 ELSE 1 END) + (CASE WHEN charge_type IS NULL THEN 0 ELSE 1 END)");
            });
            entity.HasKey(e => e.RuleId).HasName("pk_posting_rule");
            entity.Property(e => e.RuleId).HasColumnName("rule_id");
            entity.Property(e => e.RuleSetId).HasColumnName("rule_set_id");
            entity.Property(e => e.RuleCode).HasColumnName("rule_code");
            entity.Property(e => e.SourceEvent).HasColumnName("source_event");
            entity.Property(e => e.EntryType).HasColumnName("entry_type");
            entity.Property(e => e.SourceAccount).HasColumnName("source_account");
            entity.Property(e => e.ChargeCategory).HasColumnName("charge_category");
            entity.Property(e => e.ChargeType).HasColumnName("charge_type");
            entity.Property(e => e.Specificity).HasColumnName("specificity");
            entity.Property(e => e.AccountCode).HasColumnName("account_code");
            entity.Property(e => e.DeriveFrom).HasColumnName("derive_from");
            entity.Property(e => e.DescriptionEl).HasColumnName("description_el");
            entity.Property(e => e.DescriptionEn).HasColumnName("description_en");
            entity.HasIndex(e => new { e.RuleSetId, e.RuleCode }).IsUnique().HasDatabaseName("ux_posting_rule_code");
            entity.HasOne<PostingRuleSetRow>().WithMany().HasForeignKey(e => e.RuleSetId).HasConstraintName("fk_posting_rule_set").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ChargeTypeViewRow>(entity =>
        {
            entity.ToTable("charge_type_view", t => t.HasCheckConstraint("ck_charge_type_view_hash", "artefact_hash ~ '^[0-9a-f]{64}$'"));
            entity.HasKey(e => new { e.ArtefactHash, e.ChargeType }).HasName("pk_charge_type_view");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash").HasColumnType("char(64)");
            entity.Property(e => e.ChargeType).HasColumnName("charge_type");
            entity.Property(e => e.Category).HasColumnName("category");
            entity.Property(e => e.GlKey).HasColumnName("gl_key");
            entity.Property(e => e.Coverage).HasColumnName("coverage");
            entity.Property(e => e.LoadedAt).HasColumnName("loaded_at").HasColumnType("timestamptz");
        });
    }

    private static void Intake(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PolicyContextRow>(entity =>
        {
            entity.ToTable("policy_context", t => t.HasCheckConstraint("ck_policy_context_record_version", "record_version >= 1"));
            entity.HasKey(e => e.PolicyTermId).HasName("pk_policy_context");
            entity.Property(e => e.PolicyTermId).HasColumnName("policy_term_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.PolicyNumber).HasColumnName("policy_number");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.ProductVersion).HasColumnName("product_version");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash").HasColumnType("char(64)");
            entity.Property(e => e.ChargeTypesLoaded).HasColumnName("charge_types_loaded");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.Property(e => e.AggregateSequence).HasColumnName("aggregate_sequence");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.PolicyId }).HasDatabaseName("ix_policy_context_policy");
        });

        modelBuilder.Entity<BusinessEventRow>(entity =>
        {
            entity.ToTable("business_event", t =>
            {
                t.HasCheckConstraint("ck_business_event_status", In("status", BusinessEventStatus.All));
                t.HasCheckConstraint("ck_business_event_relevance", In("relevance", Relevance.All));
                t.HasCheckConstraint("ck_business_event_waiting", "(status = 'WAITING') = (waiting_on IS NOT NULL)");
                t.HasCheckConstraint("ck_business_event_suspended", "(status = 'SUSPENDED') = (exception_reason IS NOT NULL)");
                t.HasCheckConstraint("ck_business_event_posted", "status <> 'POSTED' OR cardinality(journal_ids) >= 1");
                t.HasCheckConstraint("ck_business_event_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.BusinessEventId).HasName("pk_business_event");
            entity.Property(e => e.BusinessEventId).HasColumnName("business_event_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.LegalEntityCode).HasColumnName("legal_entity_code");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction");
            entity.Property(e => e.SourceModule).HasColumnName("source_module");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.Property(e => e.EventType).HasColumnName("event_type");
            entity.Property(e => e.RegistryName).HasColumnName("registry_name");
            entity.Property(e => e.SchemaVersion).HasColumnName("schema_version");
            entity.Property(e => e.AggregateType).HasColumnName("aggregate_type");
            entity.Property(e => e.AggregateId).HasColumnName("aggregate_id");
            entity.Property(e => e.AggregateSequence).HasColumnName("aggregate_sequence");
            entity.Property(e => e.OutOfOrder).HasColumnName("out_of_order");
            entity.Property(e => e.Origin).HasColumnName("origin");
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            entity.Property(e => e.SetId).HasColumnName("set_id");
            entity.Property(e => e.SetSize).HasColumnName("set_size");
            entity.Property(e => e.SetIndex).HasColumnName("set_index");
            entity.Property(e => e.Relevance).HasColumnName("relevance");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.WaitingOn).HasColumnName("waiting_on");
            entity.Property(e => e.ExceptionReason).HasColumnName("exception_reason");
            entity.Property(e => e.ExceptionDetail).HasColumnName("exception_detail");
            entity.Property(e => e.AccountingDate).HasColumnName("accounting_date");
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(e => e.BusinessKeys).HasColumnName("business_keys").HasColumnType("jsonb");
            entity.Property(e => e.ConfigurationHash).HasColumnName("configuration_hash");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.PolicyTermId).HasColumnName("policy_term_id");
            entity.Property(e => e.BillingAccountId).HasColumnName("billing_account_id");
            entity.Property(e => e.JournalIds).HasColumnName("journal_ids");
            entity.Property(e => e.Attempts).HasColumnName("attempts");
            entity.Property(e => e.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();

            // REQ-FIN-031: one business event per source event, whatever the delivery count.
            entity.HasIndex(e => e.SourceEventId).IsUnique().HasDatabaseName("ux_business_event_source");
            entity.HasIndex(e => new { e.AggregateType, e.AggregateId, e.AggregateSequence }).HasDatabaseName("ix_business_event_aggregate");
            entity.HasIndex(e => e.WaitingOn).HasFilter("waiting_on IS NOT NULL").HasDatabaseName("ix_business_event_waiting");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_business_event_status");
        });
    }

    private static void JournalTables(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FinancialPeriodRow>(entity =>
        {
            entity.ToTable("financial_period", t =>
            {
                t.HasCheckConstraint("ck_financial_period_code", "period_code ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                t.HasCheckConstraint("ck_financial_period_status", In("status", ["OPEN"]));
            });
            entity.HasKey(e => e.PeriodId).HasName("pk_financial_period");
            entity.Property(e => e.PeriodId).HasColumnName("period_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.PeriodCode).HasColumnName("period_code");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.PeriodCode }).IsUnique().HasDatabaseName("ux_financial_period_code");
        });

        modelBuilder.Entity<JournalEntryRow>(entity =>
        {
            entity.ToTable("journal_entry", t =>
            {
                t.HasCheckConstraint("ck_journal_entry_source_type", In("source_type", [SourceTypes.Event, SourceTypes.Reversal]));
                t.HasCheckConstraint("ck_journal_entry_reversal", "(source_type = 'REVERSAL') = (reverses_journal_id IS NOT NULL)");
                t.HasCheckConstraint("ck_journal_entry_currency", "functional_currency ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(e => e.JournalId).HasName("pk_journal_entry");
            entity.Property(e => e.JournalId).HasColumnName("journal_id");
            entity.Property(e => e.JournalNumber).HasColumnName("journal_number");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.LegalEntityCode).HasColumnName("legal_entity_code");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction");
            entity.Property(e => e.Book).HasColumnName("book");
            entity.Property(e => e.AccountingDate).HasColumnName("accounting_date");
            entity.Property(e => e.BusinessDate).HasColumnName("business_date");
            entity.Property(e => e.PeriodId).HasColumnName("period_id");
            entity.Property(e => e.SourceType).HasColumnName("source_type");
            entity.Property(e => e.SourceModule).HasColumnName("source_module");
            entity.Property(e => e.SourceEventType).HasColumnName("source_event_type");
            entity.Property(e => e.SourceEventIds).HasColumnName("source_event_ids");
            entity.Property(e => e.SourceRef).HasColumnName("source_ref");
            entity.Property(e => e.RuleSetId).HasColumnName("rule_set_id");
            entity.Property(e => e.RuleSetVersion).HasColumnName("rule_set_version");
            entity.Property(e => e.RuleCodes).HasColumnName("rule_codes");
            entity.Property(e => e.ReversesJournalId).HasColumnName("reverses_journal_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.FunctionalCurrency).HasColumnName("functional_currency").HasColumnType("char(3)");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.PostedAt).HasColumnName("posted_at").HasColumnType("timestamptz");
            entity.Property(e => e.PostedBy).HasColumnName("posted_by");

            // REQ-FIN-069: unique journal number per legal entity (the JOURNAL series is gapless and yearly).
            entity.HasIndex(e => new { e.LegalEntityId, e.JournalNumber }).IsUnique().HasDatabaseName("ux_journal_entry_number");
            // REQ-FIN-073: a journal is reversed at most once.
            entity.HasIndex(e => e.ReversesJournalId).IsUnique().HasFilter("reverses_journal_id IS NOT NULL").HasDatabaseName("ux_journal_entry_reverses");
            entity.HasIndex(e => new { e.LegalEntityId, e.Book, e.AccountingDate }).HasDatabaseName("ix_journal_entry_date");
            entity.HasIndex(e => new { e.LegalEntityId, e.SourceRef }).HasDatabaseName("ix_journal_entry_source");
            entity.HasOne<FinancialPeriodRow>().WithMany().HasForeignKey(e => e.PeriodId).HasConstraintName("fk_journal_entry_period").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<JournalEntryRow>().WithMany().HasForeignKey(e => e.ReversesJournalId).HasConstraintName("fk_journal_entry_reverses").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PostingRuleSetRow>().WithMany().HasForeignKey(e => e.RuleSetId).HasConstraintName("fk_journal_entry_rule_set").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalLineRow>(entity =>
        {
            entity.ToTable("journal_line", t =>
            {
                t.HasCheckConstraint("ck_journal_line_side", In("side", [Sides.Debit, Sides.Credit]));
                t.HasCheckConstraint("ck_journal_line_amount", "amount > 0 AND amount_functional > 0");
                t.HasCheckConstraint("ck_journal_line_currency", "currency ~ '^[A-Z]{3}$' AND functional_currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_journal_line_no", "line_no >= 1");
            });
            entity.HasKey(e => e.LineId).HasName("pk_journal_line");
            entity.Property(e => e.LineId).HasColumnName("line_id");
            entity.Property(e => e.JournalId).HasColumnName("journal_id");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Book).HasColumnName("book");
            entity.Property(e => e.AccountCode).HasColumnName("account_code");
            entity.Property(e => e.Side).HasColumnName("side");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.AmountFunctional).HasColumnName("amount_functional").HasColumnType("numeric(19,4)");
            entity.Property(e => e.FunctionalCurrency).HasColumnName("functional_currency").HasColumnType("char(3)");
            entity.Property(e => e.RuleCode).HasColumnName("rule_code");
            entity.Property(e => e.BusinessDate).HasColumnName("business_date");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.ProductVersion).HasColumnName("product_version");
            entity.Property(e => e.CoverageCode).HasColumnName("coverage_code");
            entity.Property(e => e.ChargeType).HasColumnName("charge_type");
            entity.Property(e => e.ChargeCategory).HasColumnName("charge_category");
            entity.Property(e => e.GlKey).HasColumnName("gl_key");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.PolicyNumber).HasColumnName("policy_number");
            entity.Property(e => e.PolicyTermId).HasColumnName("policy_term_id");
            entity.Property(e => e.PolicyTransactionId).HasColumnName("policy_transaction_id");
            entity.Property(e => e.ChargeId).HasColumnName("charge_id");
            entity.Property(e => e.BillingAccountId).HasColumnName("billing_account_id");
            entity.Property(e => e.InvoiceId).HasColumnName("invoice_id");
            entity.Property(e => e.ReceiptId).HasColumnName("receipt_id");
            entity.HasIndex(e => new { e.JournalId, e.LineNo }).IsUnique().HasDatabaseName("ux_journal_line_no");
            entity.HasIndex(e => new { e.LegalEntityId, e.PolicyNumber }).HasFilter("policy_number IS NOT NULL").HasDatabaseName("ix_journal_line_policy");
            entity.HasOne<JournalEntryRow>().WithMany().HasForeignKey(e => e.JournalId).HasConstraintName("fk_journal_line_entry").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Finance</c> (no connection opened).</summary>
internal sealed class FinanceDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FinanceDbContext>
{
    public FinanceDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<FinanceDbContext>("Host=localhost;Database=coreins_design;Username=design", FinanceModule.Schema));
}
