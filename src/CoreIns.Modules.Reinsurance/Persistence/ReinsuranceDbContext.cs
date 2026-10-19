using CoreIns.Modules.Reinsurance.Cession;
using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// The Reinsurance module's EF Core context (schema <c>ri</c>), sharing the scope's <see cref="DbSession"/> connection and
/// transaction so contract rows, outbox events and audit records commit together. The exclusion constraint on the valid
/// period and the immutability / append-only triggers are created by the first migration in SQL
/// (<see cref="ReinsuranceDatabaseSql"/>).
/// </summary>
internal sealed class ReinsuranceDbContext(DbContextOptions<ReinsuranceDbContext> options) : ModuleDbContext(options)
{
    public DbSet<ContractRow> Contracts => Set<ContractRow>();

    public DbSet<ContractVersionRow> Versions => Set<ContractVersionRow>();

    public DbSet<SectionRow> Sections => Set<SectionRow>();

    public DbSet<LayerRow> Layers => Set<LayerRow>();

    public DbSet<ClauseRow> Clauses => Set<ClauseRow>();

    public DbSet<ParticipationRow> Participations => Set<ParticipationRow>();

    public DbSet<ClaimViewRow> ClaimViews => Set<ClaimViewRow>();

    public DbSet<ClaimEventRow> ClaimEvents => Set<ClaimEventRow>();

    public DbSet<ClaimExposureRow> ClaimExposures => Set<ClaimExposureRow>();

    public DbSet<ClaimAmountRow> ClaimAmounts => Set<ClaimAmountRow>();

    public DbSet<RecoveryBatchRow> RecoveryBatches => Set<RecoveryBatchRow>();

    public DbSet<RecoveryRow> Recoveries => Set<RecoveryRow>();

    public DbSet<IntakeExceptionRow> IntakeExceptions => Set<IntakeExceptionRow>();

    public DbSet<PremiumDeltaRow> PremiumDeltas => Set<PremiumDeltaRow>();

    public DbSet<CessionRow> Cessions => Set<CessionRow>();

    public DbSet<CessionExceptionRow> CessionExceptions => Set<CessionExceptionRow>();

    public DbSet<TrackerRow> Trackers => Set<TrackerRow>();

    protected override string Schema => ReinsuranceModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureRecovery(modelBuilder);
        ConfigureCession(modelBuilder);

        modelBuilder.Entity<ContractRow>(entity =>
        {
            entity.ToTable("contract", table =>
            {
                table.HasCheckConstraint("ck_contract_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("ck_contract_record_version", "record_version >= 1");
                table.HasCheckConstraint("ck_contract_type", "contract_type IN ('XOL_PER_RISK')");
                table.HasCheckConstraint("ck_contract_currency", "currency = 'EUR'");
                table.HasCheckConstraint("ck_contract_year", "contract_year BETWEEN 1990 AND 2200");
                table.HasCheckConstraint("ck_contract_status", "status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'EXPIRED', 'CLOSED')");
                table.HasCheckConstraint(
                    "ck_contract_submitted_shape",
                    "status NOT IN ('PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'EXPIRED') OR (submitted_by IS NOT NULL AND approval_request_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_contract_decided_shape", "status NOT IN ('APPROVED', 'ACTIVE', 'EXPIRED') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)");
                table.HasCheckConstraint("ck_contract_activated_shape", "status NOT IN ('ACTIVE', 'EXPIRED') OR activated_at IS NOT NULL");
                table.HasCheckConstraint("ck_contract_expired_shape", "status <> 'EXPIRED' OR expired_at IS NOT NULL");
                table.HasCheckConstraint("ck_contract_participants", "cardinality(participants) >= 1");
            });
            entity.HasKey(e => e.ContractId).HasName("pk_contract");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.ContractNumber).HasColumnName("contract_number");
            entity.Property(e => e.StableTreatyId).HasColumnName("stable_treaty_id");
            entity.Property(e => e.ContractType).HasColumnName("contract_type");
            entity.Property(e => e.ContractYear).HasColumnName("contract_year");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Participants).HasColumnName("participants");
            entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.ReturnReason).HasColumnName("return_reason");
            entity.Property(e => e.ActivatedAt).HasColumnName("activated_at").HasColumnType("timestamptz");
            entity.Property(e => e.ExpiredAt).HasColumnName("expired_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.ContractNumber }).IsUnique().HasDatabaseName("ux_contract_number");
            entity.HasIndex(e => new { e.LegalEntityId, e.StableTreatyId, e.ContractYear }).IsUnique().HasDatabaseName("ux_contract_treaty_year");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_contract_status");
        });

        modelBuilder.Entity<ContractVersionRow>(entity =>
        {
            entity.ToTable("contract_version", table =>
            {
                table.HasCheckConstraint("ck_contract_version_no", "version_no >= 1");
                table.HasCheckConstraint("ck_contract_version_period", "valid_to > valid_from");
                table.HasCheckConstraint("ck_contract_version_known", "known_to IS NULL OR known_to > known_from");
                table.HasCheckConstraint("ck_contract_version_placed", "placed_pct > 0 AND placed_pct <= 100");
                table.HasCheckConstraint("ck_contract_version_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_contract_version_rev", "content_rev >= 1");
                table.HasCheckConstraint("ck_contract_version_approved", "(approved_at IS NULL) = (approved_by IS NULL)");
            });
            entity.HasKey(e => e.VersionId).HasName("pk_contract_version");
            entity.Property(e => e.VersionId).HasColumnName("version_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.KnownFrom).HasColumnName("known_from").HasColumnType("timestamptz");
            entity.Property(e => e.KnownTo).HasColumnName("known_to").HasColumnType("timestamptz");
            entity.Property(e => e.ContentRev).HasColumnName("content_rev");
            entity.Property(e => e.PlacedPct).HasColumnName("placed_pct").HasColumnType("numeric(9,6)");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.ContractId, e.VersionNo }).IsUnique().HasDatabaseName("ux_contract_version_no");
            entity.HasOne<ContractRow>().WithMany().HasForeignKey(e => e.ContractId).HasConstraintName("fk_contract_version_contract").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SectionRow>(entity =>
        {
            entity.ToTable("section", table =>
            {
                table.HasCheckConstraint("ck_section_scope", "cardinality(product_codes) >= 1 AND cardinality(coverage_codes) >= 1");
                table.HasCheckConstraint("ck_section_rev", "rev >= 1 AND section_no >= 1");
            });
            entity.HasKey(e => e.SectionId).HasName("pk_section");
            entity.Property(e => e.SectionId).HasColumnName("section_id");
            entity.Property(e => e.VersionId).HasColumnName("version_id");
            entity.Property(e => e.Rev).HasColumnName("rev");
            entity.Property(e => e.SectionNo).HasColumnName("section_no");
            entity.Property(e => e.ProductCodes).HasColumnName("product_codes");
            entity.Property(e => e.CoverageCodes).HasColumnName("coverage_codes");
            entity.HasIndex(e => new { e.VersionId, e.Rev, e.SectionNo }).IsUnique().HasDatabaseName("ux_section_no");
            entity.HasOne<ContractVersionRow>().WithMany().HasForeignKey(e => e.VersionId).HasConstraintName("fk_section_version").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LayerRow>(entity =>
        {
            entity.ToTable("layer", table =>
            {
                table.HasCheckConstraint("ck_layer_amounts", "attachment >= 0 AND limit_amount > 0 AND aad >= 0 AND (aal IS NULL OR aal > 0)");
                table.HasCheckConstraint("ck_layer_currency", "currency = 'EUR'");
                table.HasCheckConstraint("ck_layer_no", "layer_no >= 1 AND rev >= 1");
            });
            entity.HasKey(e => e.LayerId).HasName("pk_layer");
            entity.Property(e => e.LayerId).HasColumnName("layer_id");
            entity.Property(e => e.SectionId).HasColumnName("section_id");
            entity.Property(e => e.VersionId).HasColumnName("version_id");
            entity.Property(e => e.Rev).HasColumnName("rev");
            entity.Property(e => e.LayerNo).HasColumnName("layer_no");
            entity.Property(e => e.Attachment).HasColumnName("attachment").HasColumnType("numeric(19,4)");
            entity.Property(e => e.LimitAmount).HasColumnName("limit_amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Aad).HasColumnName("aad").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Aal).HasColumnName("aal").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.HasIndex(e => new { e.SectionId, e.LayerNo }).IsUnique().HasDatabaseName("ux_layer_no");
            entity.HasIndex(e => new { e.VersionId, e.Rev }).HasDatabaseName("ix_layer_version_rev");
            entity.HasOne<SectionRow>().WithMany().HasForeignKey(e => e.SectionId).HasConstraintName("fk_layer_section").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ContractVersionRow>().WithMany().HasForeignKey(e => e.VersionId).HasConstraintName("fk_layer_version").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ClauseRow>(entity =>
        {
            entity.ToTable("clause", table =>
            {
                table.HasCheckConstraint("ck_clause_inure", "recoveries_inure IN ('REALISED_ONLY')");
                table.HasCheckConstraint("ck_clause_rev", "rev >= 1");
            });
            entity.HasKey(e => e.ClauseId).HasName("pk_clause");
            entity.Property(e => e.ClauseId).HasColumnName("clause_id");
            entity.Property(e => e.VersionId).HasColumnName("version_id");
            entity.Property(e => e.Rev).HasColumnName("rev");
            entity.Property(e => e.AlaeIncluded).HasColumnName("alae_included");
            entity.Property(e => e.StatutoryInterestIncluded).HasColumnName("statutory_interest_included");
            entity.Property(e => e.RecoveriesInure).HasColumnName("recoveries_inure");
            entity.HasIndex(e => new { e.VersionId, e.Rev }).IsUnique().HasDatabaseName("ux_clause_version_rev");
            entity.HasOne<ContractVersionRow>().WithMany().HasForeignKey(e => e.VersionId).HasConstraintName("fk_clause_version").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ParticipationRow>(entity =>
        {
            entity.ToTable("participation", table =>
            {
                table.HasCheckConstraint("ck_participation_line", "signed_line_pct > 0 AND signed_line_pct <= 100");
                table.HasCheckConstraint("ck_participation_rev", "rev >= 1");
            });
            entity.HasKey(e => e.ParticipationId).HasName("pk_participation");
            entity.Property(e => e.ParticipationId).HasColumnName("participation_id");
            entity.Property(e => e.VersionId).HasColumnName("version_id");
            entity.Property(e => e.Rev).HasColumnName("rev");
            entity.Property(e => e.ReinsurerPartyId).HasColumnName("reinsurer_party_id");
            entity.Property(e => e.BrokerPartyId).HasColumnName("broker_party_id");
            entity.Property(e => e.SignedLinePct).HasColumnName("signed_line_pct").HasColumnType("numeric(9,6)");
            entity.Property(e => e.Lead).HasColumnName("lead");
            entity.HasIndex(e => new { e.VersionId, e.Rev, e.ReinsurerPartyId }).IsUnique().HasDatabaseName("ux_participation_reinsurer");
            entity.HasIndex(e => new { e.VersionId, e.Rev }).IsUnique().HasFilter("lead").HasDatabaseName("ux_participation_one_lead");
            entity.HasIndex(e => e.ReinsurerPartyId).HasDatabaseName("ix_participation_reinsurer");
            entity.HasOne<ContractVersionRow>().WithMany().HasForeignKey(e => e.VersionId).HasConstraintName("fk_participation_version").OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureCession(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PremiumDeltaRow>(entity =>
        {
            entity.ToTable("premium_delta");
            entity.HasKey(e => e.EventId).HasName("pk_premium_delta");
            entity.Property(e => e.EventId).HasColumnName("event_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.CoverageCode).HasColumnName("coverage_code");
            entity.Property(e => e.ChargeCategory).HasColumnName("charge_category");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from").HasColumnType("date");
            entity.Property(e => e.SetId).HasColumnName("set_id");
            entity.Property(e => e.SetSize).HasColumnName("set_size");
            entity.Property(e => e.CorrectionOf).HasColumnName("correction_of");
            entity.Property(e => e.OosSetId).HasColumnName("oos_set_id");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.TermId).HasDatabaseName("ix_premium_delta_term");
            entity.HasIndex(e => e.SetId).HasDatabaseName("ix_premium_delta_set");
        });

        modelBuilder.Entity<CessionRow>(entity =>
        {
            entity.ToTable("cession", table => table.HasCheckConstraint("ck_cession_mode", "mode IN ('NET', 'GROSS')"));
            entity.HasKey(e => e.CessionId).HasName("pk_cession");
            entity.Property(e => e.CessionId).HasColumnName("cession_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.TreatyId).HasColumnName("treaty_id");
            entity.Property(e => e.ContractYear).HasColumnName("contract_year");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.SetKey).HasColumnName("set_key");
            entity.Property(e => e.WrittenPremium).HasColumnName("written_premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Target).HasColumnName("target").HasColumnType("numeric(19,4)");
            entity.Property(e => e.BookedBefore).HasColumnName("booked_before").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Delta).HasColumnName("delta").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Mode).HasColumnName("mode");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.TermId, e.TreatyId, e.SetKey }).IsUnique().HasDatabaseName("ux_cession_set");
        });

        modelBuilder.Entity<CessionExceptionRow>(entity =>
        {
            entity.ToTable("cession_exception");
            entity.HasKey(e => e.ExceptionId).HasName("pk_cession_exception");
            entity.Property(e => e.ExceptionId).HasColumnName("exception_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.TermId).HasColumnName("term_id");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.Detail).HasColumnName("detail");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.TermId).HasDatabaseName("ix_cession_exception_term");
        });

        modelBuilder.Entity<TrackerRow>(entity =>
        {
            entity.ToTable("tracker", table => table.HasCheckConstraint("ck_tracker_amount", "opening_aggregate >= 0"));
            entity.HasKey(e => e.TrackerId).HasName("pk_tracker");
            entity.Property(e => e.TrackerId).HasColumnName("tracker_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.TreatyId).HasColumnName("treaty_id");
            entity.Property(e => e.ContractYear).HasColumnName("contract_year");
            entity.Property(e => e.OpeningAggregate).HasColumnName("opening_aggregate").HasColumnType("numeric(19,4)");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.TreatyId, e.ContractYear }).IsUnique().HasDatabaseName("ux_tracker_treaty_year");
        });
    }

    private static void ConfigureRecovery(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClaimViewRow>(entity =>
        {
            entity.ToTable("claim_view");
            entity.HasKey(e => e.ClaimId).HasName("pk_claim_view");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.LossDate).HasColumnName("loss_date").HasColumnType("date");
            entity.Property(e => e.Closed).HasColumnName("closed");
            entity.Property(e => e.ClosedSequence).HasColumnName("closed_sequence");
            entity.Property(e => e.LastSequence).HasColumnName("last_sequence");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<ClaimEventRow>(entity =>
        {
            entity.ToTable("claim_event");
            entity.HasKey(e => e.EventId).HasName("pk_claim_event");
            entity.Property(e => e.EventId).HasColumnName("event_id");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.AggregateSequence).HasColumnName("aggregate_sequence");
            entity.Property(e => e.AppliedAt).HasColumnName("applied_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.ClaimId).HasDatabaseName("ix_claim_event_claim");
        });

        modelBuilder.Entity<ClaimExposureRow>(entity =>
        {
            entity.ToTable("claim_exposure");
            entity.HasKey(e => new { e.ClaimId, e.ExposureId }).HasName("pk_claim_exposure");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.ExposureId).HasColumnName("exposure_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.CoverageCode).HasColumnName("coverage_code");
            entity.Property(e => e.SiiLob).HasColumnName("sii_lob");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.HasIndex(e => e.ContractId).HasDatabaseName("ix_claim_exposure_contract");
        });

        modelBuilder.Entity<ClaimAmountRow>(entity =>
        {
            entity.ToTable("claim_amount");
            entity.HasKey(e => new { e.ClaimId, e.ExposureId, e.CostType }).HasName("pk_claim_amount");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.ExposureId).HasColumnName("exposure_id");
            entity.Property(e => e.CostType).HasColumnName("cost_type");
            entity.Property(e => e.Paid).HasColumnName("paid").HasColumnType("numeric(19,4)");
            entity.Property(e => e.OpenReserve).HasColumnName("open_reserve").HasColumnType("numeric(19,4)");
            entity.Property(e => e.RecoveryOpen).HasColumnName("recovery_open").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Realised).HasColumnName("realised").HasColumnType("numeric(19,4)");
            entity.Property(e => e.ReserveSequence).HasColumnName("reserve_sequence");
            entity.Property(e => e.RecoverySequence).HasColumnName("recovery_sequence");
        });

        modelBuilder.Entity<RecoveryBatchRow>(entity =>
        {
            entity.ToTable("recovery_batch");
            entity.HasKey(e => e.BatchId).HasName("pk_recovery_batch");
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.ContractYear).HasColumnName("contract_year");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.Property(e => e.CalcHash).HasColumnName("calc_hash");
            entity.Property(e => e.EngineVersion).HasColumnName("engine_version");
            entity.Property(e => e.ContractVersion).HasColumnName("contract_version");
            entity.Property(e => e.Trace).HasColumnName("trace").HasColumnType("jsonb");
            entity.HasIndex(e => new { e.ContractId, e.RecordedAt }).HasDatabaseName("ix_recovery_batch_contract");
        });

        modelBuilder.Entity<RecoveryRow>(entity =>
        {
            entity.ToTable("recovery", table => table.HasCheckConstraint("ck_recovery_state", "state IN ('CALCULATED', 'POSTED', 'REVERSED')"));
            entity.HasKey(e => e.RecoveryId).HasName("pk_recovery");
            entity.Property(e => e.RecoveryId).HasColumnName("recovery_id");
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.ContractYear).HasColumnName("contract_year");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.LayerId).HasColumnName("layer_id");
            entity.Property(e => e.LayerNo).HasColumnName("layer_no");
            entity.Property(e => e.ParticipantPartyId).HasColumnName("participant_party_id");
            entity.Property(e => e.IncurredDelta).HasColumnName("incurred_delta").HasColumnType("numeric(19,4)");
            entity.Property(e => e.PaidDelta).HasColumnName("paid_delta").HasColumnType("numeric(19,4)");
            entity.Property(e => e.OutstandingDelta).HasColumnName("outstanding_delta").HasColumnType("numeric(19,4)");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.SiiLob).HasColumnName("sii_lob");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.ContractId, e.ClaimId }).HasDatabaseName("ix_recovery_contract_claim");
            entity.HasIndex(e => e.ClaimId).HasDatabaseName("ix_recovery_claim");
            entity.HasIndex(e => e.BatchId).HasDatabaseName("ix_recovery_batch");
            entity.HasOne<RecoveryBatchRow>().WithMany().HasForeignKey(e => e.BatchId).HasConstraintName("fk_recovery_batch").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IntakeExceptionRow>(entity =>
        {
            entity.ToTable("intake_exception");
            entity.HasKey(e => e.ExceptionId).HasName("pk_intake_exception");
            entity.Property(e => e.ExceptionId).HasColumnName("exception_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.Detail).HasColumnName("detail");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.ClaimId).HasDatabaseName("ix_intake_exception_claim");
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Reinsurance</c> (no connection opened).</summary>
internal sealed class ReinsuranceDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ReinsuranceDbContext>
{
    public ReinsuranceDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<ReinsuranceDbContext>("Host=localhost;Database=coreins_design;Username=design", ReinsuranceModule.Schema));
}
