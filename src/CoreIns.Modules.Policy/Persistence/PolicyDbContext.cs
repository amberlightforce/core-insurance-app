using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreIns.Modules.Policy.Persistence;

/// <summary>
/// The Policy module's EF Core context (schema <c>pol</c>), sharing the scope's <see cref="DbSession"/> connection and
/// transaction so policy rows, outbox events and audit records commit together. The bitemporal non-overlap rules
/// (PostgreSQL 17 <c>btree_gist</c> exclusion constraints, D-ARC-05) and the append-only triggers are created by the
/// migration in SQL: EF Core cannot model them.
/// </summary>
internal sealed class PolicyDbContext(DbContextOptions<PolicyDbContext> options) : ModuleDbContext(options)
{
    public DbSet<JobRow> Jobs => Set<JobRow>();

    public DbSet<QuoteVersionRow> QuoteVersions => Set<QuoteVersionRow>();

    public DbSet<PolicyRow> Policies => Set<PolicyRow>();

    public DbSet<PolicyTermRow> Terms => Set<PolicyTermRow>();

    public DbSet<PolicyTransactionRow> Transactions => Set<PolicyTransactionRow>();

    public DbSet<SegmentRow> Segments => Set<SegmentRow>();

    public DbSet<ChargeLineRow> ChargeLines => Set<ChargeLineRow>();

    protected override string Schema => PolicyModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<JobRow>(entity =>
        {
            entity.ToTable("job", table =>
            {
                table.HasCheckConstraint("ck_job_state", Codes.CheckSql<JobState>("state"));
                table.HasCheckConstraint("ck_job_type", Codes.CheckSql<JobType>("job_type"));
                table.HasCheckConstraint("ck_job_quote_type", "quote_type IN ('QUICK', 'FULL')");
                table.HasCheckConstraint("ck_job_period", "expiration_at > effective_at");
                table.HasCheckConstraint("ck_job_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("ck_job_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_job_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.JobId).HasName("pk_job");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.JobNumber).HasColumnName("job_number");
            entity.Property(e => e.JobType).HasColumnName("job_type");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.Referred).HasColumnName("referred");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.PolicyholderPartyId).HasColumnName("policyholder_party_id");
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.ProductVersion).HasColumnName("product_version");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash");
            entity.Property(e => e.RatingArtefactHash).HasColumnName("rating_artefact_hash");
            entity.Property(e => e.ResolutionHash).HasColumnName("resolution_hash");
            entity.Property(e => e.ResolutionManifest).HasColumnName("resolution_manifest").HasColumnType("jsonb");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.ProducerCode).HasColumnName("producer_code");
            entity.Property(e => e.QuoteType).HasColumnName("quote_type");
            entity.Property(e => e.EffectiveAt).HasColumnName("effective_at").HasColumnType("timestamptz");
            entity.Property(e => e.ExpirationAt).HasColumnName("expiration_at").HasColumnType("timestamptz");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.CurrentVersionNo).HasColumnName("current_version_no");
            entity.Property(e => e.BoundTransactionId).HasColumnName("bound_transaction_id");
            entity.Property(e => e.DeclineId).HasColumnName("decline_id");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.JobNumber }).IsUnique().HasDatabaseName("ux_job_number");
            entity.HasIndex(e => e.PolicyId).HasDatabaseName("ix_job_policy");
            entity.HasIndex(e => new { e.LegalEntityId, e.PolicyholderPartyId }).HasDatabaseName("ix_job_policyholder");
        });

        modelBuilder.Entity<QuoteVersionRow>(entity =>
        {
            entity.ToTable("quote_version", table =>
            {
                table.HasCheckConstraint("ck_quote_version_state", Codes.CheckSql<QuoteState>("state"));
                table.HasCheckConstraint("ck_quote_version_no", "version_no >= 1 AND version_no <= 20");
                table.HasCheckConstraint("ck_quote_version_draft", "draft_version >= 0");
                table.HasCheckConstraint("ck_quote_version_quoted", "state <> 'QUOTED' OR (quoted_at IS NOT NULL AND charges IS NOT NULL AND total IS NOT NULL)");
            });
            entity.HasKey(e => e.QuoteId).HasName("pk_quote_version");
            entity.Property(e => e.QuoteId).HasColumnName("quote_id");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.DraftVersion).HasColumnName("draft_version");
            entity.Property(e => e.RiskTree).HasColumnName("risk_tree").HasColumnType("jsonb");
            entity.Property(e => e.WorksheetId).HasColumnName("worksheet_id");
            entity.Property(e => e.WorksheetHash).HasColumnName("worksheet_hash");
            entity.Property(e => e.Bindable).HasColumnName("bindable");
            entity.Property(e => e.Charges).HasColumnName("charges").HasColumnType("jsonb");
            entity.Property(e => e.Premium).HasColumnName("premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Taxes).HasColumnName("taxes").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Total).HasColumnName("total").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Issues).HasColumnName("issues").HasColumnType("jsonb");
            entity.Property(e => e.UwEvaluationId).HasColumnName("uw_evaluation_id");
            entity.Property(e => e.ConfigurationHash).HasColumnName("configuration_hash");
            entity.Property(e => e.QuotedAt).HasColumnName("quoted_at").HasColumnType("timestamptz");
            entity.Property(e => e.ValidUntil).HasColumnName("valid_until").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.JobId, e.VersionNo }).IsUnique().HasDatabaseName("ux_quote_version_job_no");
            entity.HasOne<JobRow>().WithMany().HasForeignKey(e => e.JobId).HasConstraintName("fk_quote_version_job").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PolicyRow>(entity =>
        {
            entity.ToTable("policy", table => table.HasCheckConstraint("ck_policy_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'"));
            entity.HasKey(e => e.PolicyId).HasName("pk_policy");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.PolicyNumber).HasColumnName("policy_number");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.PolicyholderPartyId).HasColumnName("policyholder_party_id");
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.PolicyNumber }).IsUnique().HasDatabaseName("ux_policy_number");
            entity.HasIndex(e => new { e.LegalEntityId, e.PolicyholderPartyId }).HasDatabaseName("ix_policy_policyholder");
        });

        modelBuilder.Entity<PolicyTermRow>(entity =>
        {
            entity.ToTable("policy_term", table =>
            {
                table.HasCheckConstraint("ck_policy_term_state", Codes.CheckSql<PolicyTermState>("state"));
                table.HasCheckConstraint("ck_policy_term_number", "term_number >= 1");
                Bitemporal(table, "policy_term");
            });
            entity.HasKey(e => e.TermVersionId).HasName("pk_policy_term");
            entity.Property(e => e.TermVersionId).HasColumnName("term_version_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.TermNumber).HasColumnName("term_number");
            MapPeriods(entity, e => e.ValidFrom, e => e.ValidTo, e => e.RecordedFrom, e => e.RecordedTo);
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.ProductVersion).HasColumnName("product_version");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash");
            entity.Property(e => e.RatingArtefactHash).HasColumnName("rating_artefact_hash");
            entity.Property(e => e.ResolutionHash).HasColumnName("resolution_hash");
            entity.Property(e => e.ConfigurationHash).HasColumnName("configuration_hash");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.ProducerCode).HasColumnName("producer_code");
            entity.Property(e => e.PaymentPlanRef).HasColumnName("payment_plan_ref");
            entity.Property(e => e.WrittenDate).HasColumnName("written_date");
            entity.Property(e => e.HeadTransactionId).HasColumnName("head_transaction_id");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");

            // One current version per term; (policy, term number) of current versions is unique (REQ-POL-032).
            entity.HasIndex(e => e.TermId).IsUnique().HasFilter("recorded_to IS NULL").HasDatabaseName("ux_policy_term_current");
            entity.HasIndex(e => new { e.PolicyId, e.TermNumber }).IsUnique().HasFilter("recorded_to IS NULL").HasDatabaseName("ux_policy_term_number");
            entity.HasOne<PolicyRow>().WithMany().HasForeignKey(e => e.PolicyId).HasConstraintName("fk_policy_term_policy").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PolicyTransactionRow>(entity =>
        {
            entity.ToTable("policy_transaction", table =>
            {
                table.HasCheckConstraint("ck_policy_transaction_kind", Codes.CheckSql<PolicyTransactionKind>("kind"));
                table.HasCheckConstraint("ck_policy_transaction_sequence", "sequence >= 1");
                table.HasCheckConstraint("ck_policy_transaction_totals", "total = premium + taxes");
            });
            entity.HasKey(e => e.TransactionId).HasName("pk_policy_transaction");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.Sequence).HasColumnName("sequence");
            entity.Property(e => e.EffectiveAt).HasColumnName("effective_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.Property(e => e.ConfigurationHash).HasColumnName("configuration_hash");
            entity.Property(e => e.ArtefactHash).HasColumnName("artefact_hash");
            entity.Property(e => e.RatingArtefactHash).HasColumnName("rating_artefact_hash");
            entity.Property(e => e.ResolutionHash).HasColumnName("resolution_hash");
            entity.Property(e => e.WorksheetId).HasColumnName("worksheet_id");
            entity.Property(e => e.Intent).HasColumnName("intent").HasColumnType("jsonb");
            entity.Property(e => e.Premium).HasColumnName("premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Taxes).HasColumnName("taxes").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Total).HasColumnName("total").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Actor).HasColumnName("actor");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.Origin).HasColumnName("origin");
            entity.HasIndex(e => new { e.PolicyId, e.Sequence }).IsUnique().HasDatabaseName("ux_policy_transaction_sequence");
            entity.HasIndex(e => e.JobId).IsUnique().HasDatabaseName("ux_policy_transaction_job");
            entity.HasOne<PolicyRow>().WithMany().HasForeignKey(e => e.PolicyId).HasConstraintName("fk_policy_transaction_policy").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SegmentRow>(entity =>
        {
            entity.ToTable("segment", table =>
            {
                Bitemporal(table, "segment");
                table.HasCheckConstraint("ck_segment_snapshot_hash", "snapshot_hash ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(e => e.SegmentId).HasName("pk_segment");
            entity.Property(e => e.SegmentId).HasColumnName("segment_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            MapPeriods(entity, e => e.ValidFrom, e => e.ValidTo, e => e.RecordedFrom, e => e.RecordedTo);
            entity.Property(e => e.SnapshotHash).HasColumnName("snapshot_hash");
            entity.Property(e => e.Snapshot).HasColumnName("snapshot").HasColumnType("jsonb");
            entity.Property(e => e.WorksheetId).HasColumnName("worksheet_id");
            entity.HasIndex(e => e.TermId).HasDatabaseName("ix_segment_term");
            entity.HasOne<PolicyTransactionRow>().WithMany().HasForeignKey(e => e.TransactionId)
                .HasConstraintName("fk_segment_transaction").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ChargeLineRow>(entity =>
        {
            entity.ToTable("charge_line", table =>
            {
                table.HasCheckConstraint("ck_charge_line_set", "set_index >= 1 AND set_index <= set_size");
                table.HasCheckConstraint("ck_charge_line_valid", "valid_to > valid_from");
                table.HasCheckConstraint("ck_charge_line_currency", "currency ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(e => e.ChargeId).HasName("pk_charge_line");
            entity.Property(e => e.ChargeId).HasColumnName("charge_id");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ElementLocator).HasColumnName("element_locator");
            entity.Property(e => e.CoverageCode).HasColumnName("coverage_code");
            entity.Property(e => e.ChargeType).HasColumnName("charge_type");
            entity.Property(e => e.ChargeCategory).HasColumnName("charge_category");
            entity.Property(e => e.DeltaKind).HasColumnName("delta_kind");
            entity.Property(e => e.AnnualRate).HasColumnName("annual_rate").HasColumnType("numeric");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.BookingDate).HasColumnName("booking_date");
            entity.Property(e => e.CorrelationKey).HasColumnName("correlation_key");
            entity.Property(e => e.SetIndex).HasColumnName("set_index");
            entity.Property(e => e.SetSize).HasColumnName("set_size");
            entity.Property(e => e.TaxTreatmentRef).HasColumnName("tax_treatment_ref");
            entity.Property(e => e.LegalStatus).HasColumnName("legal_status");
            entity.Property(e => e.Provisional).HasColumnName("provisional");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.TransactionId, e.SetIndex }).IsUnique().HasDatabaseName("ux_charge_line_set");
            entity.HasIndex(e => e.TermId).HasDatabaseName("ix_charge_line_term");
            entity.HasOne<PolicyTransactionRow>().WithMany().HasForeignKey(e => e.TransactionId)
                .HasConstraintName("fk_charge_line_transaction").OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void Bitemporal(TableBuilder table, string name)
    {
        table.HasCheckConstraint($"ck_{name}_valid", "valid_to > valid_from");
        table.HasCheckConstraint($"ck_{name}_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
    }

    private static void MapPeriods<T>(
        EntityTypeBuilder<T> entity,
        System.Linq.Expressions.Expression<Func<T, CoreIns.SharedKernel.Instant>> validFrom,
        System.Linq.Expressions.Expression<Func<T, CoreIns.SharedKernel.Instant>> validTo,
        System.Linq.Expressions.Expression<Func<T, CoreIns.SharedKernel.Instant>> recordedFrom,
        System.Linq.Expressions.Expression<Func<T, CoreIns.SharedKernel.Instant?>> recordedTo)
        where T : class
    {
        entity.Property(validFrom).HasColumnName("valid_from").HasColumnType("timestamptz");
        entity.Property(validTo).HasColumnName("valid_to").HasColumnType("timestamptz");
        entity.Property(recordedFrom).HasColumnName("recorded_from").HasColumnType("timestamptz");
        entity.Property(recordedTo).HasColumnName("recorded_to").HasColumnType("timestamptz");
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Policy</c> (no connection opened).</summary>
internal sealed class PolicyDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PolicyDbContext>
{
    public PolicyDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<PolicyDbContext>("Host=localhost;Database=coreins_design;Username=design", PolicyModule.Schema));
}
