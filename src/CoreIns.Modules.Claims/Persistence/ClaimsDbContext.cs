using CoreIns.Modules.Claims.Domain;
using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreIns.Modules.Claims.Persistence;

/// <summary>
/// The Claims module's EF Core context (schema <c>clm</c>), sharing the scope's <see cref="DbSession"/> connection and
/// transaction so claim rows, outbox events and audit records commit together. Immutability triggers (FNOL snapshot,
/// claim identity columns) are created by the migration in SQL.
/// </summary>
internal sealed class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : ModuleDbContext(options)
{
    public DbSet<ClaimRow> Claims => Set<ClaimRow>();

    public DbSet<FnolSnapshotRow> FnolSnapshots => Set<FnolSnapshotRow>();

    public DbSet<ClaimantRow> Claimants => Set<ClaimantRow>();

    public DbSet<IncidentRow> Incidents => Set<IncidentRow>();

    public DbSet<ExposureRow> Exposures => Set<ExposureRow>();

    public DbSet<ReserveLineRow> ReserveLines => Set<ReserveLineRow>();

    public DbSet<TransactionSetRow> TransactionSets => Set<TransactionSetRow>();

    public DbSet<FinancialTransactionRow> FinancialTransactions => Set<FinancialTransactionRow>();

    public DbSet<ClaimPaymentRow> ClaimPayments => Set<ClaimPaymentRow>();

    public DbSet<PayeeAccountViewRow> PayeeAccounts => Set<PayeeAccountViewRow>();

    public DbSet<SetApprovalRow> SetApprovals => Set<SetApprovalRow>();

    public DbSet<ReverificationRow> Reverifications => Set<ReverificationRow>();

    protected override string Schema => ClaimsModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ClaimRow>(entity =>
        {
            entity.ToTable("claim", table =>
            {
                CommonChecks(table, "claim");
                table.HasCheckConstraint("ck_claim_status", "status IN ('DRAFT', 'OPEN', 'CLOSED')");
                table.HasCheckConstraint("ck_claim_sub_status", "sub_status IS NULL OR sub_status IN ('NEW', 'IN_PROGRESS', 'UNDER_INVESTIGATION', 'SETTLED')");
                table.HasCheckConstraint("ck_claim_outcome", "outcome IS NULL OR " + Codes.CheckSql<ClaimOutcomeCode>("outcome"));
                table.HasCheckConstraint("ck_claim_open_shape", "status <> 'OPEN' OR (sub_status IS NOT NULL AND outcome IS NULL AND closed_at IS NULL)");
                table.HasCheckConstraint("ck_claim_closed_shape", "status <> 'CLOSED' OR (sub_status IS NULL AND outcome IS NOT NULL AND closed_at IS NOT NULL)");
                table.HasCheckConstraint("ck_claim_snapshot_status", Codes.CheckSql<SnapshotStatus>("snapshot_status"));
                table.HasCheckConstraint("ck_claim_notice_after_loss", "notice_on >= loss_date");
                table.HasCheckConstraint("ck_claim_exposure_sequence", "last_exposure_sequence >= 0");
                table.HasCheckConstraint("ck_claim_transaction_sequence", "last_transaction_sequence >= 0");
            });
            entity.HasKey(e => e.ClaimId).HasName("pk_claim");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            MapCommon(entity);
            entity.Property(e => e.ClaimNumber).HasColumnName("claim_number");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.PolicyNumber).HasColumnName("policy_number");
            entity.Property(e => e.InsuredPartyId).HasColumnName("insured_party_id");
            entity.Property(e => e.SnapshotRef).HasColumnName("snapshot_ref");
            entity.Property(e => e.SnapshotSegmentId).HasColumnName("snapshot_segment_id");
            entity.Property(e => e.PolicyTermId).HasColumnName("policy_term_id");
            entity.Property(e => e.SnapshotValidAt).HasColumnName("snapshot_valid_at").HasColumnType("timestamptz");
            entity.Property(e => e.SnapshotKnownAt).HasColumnName("snapshot_known_at").HasColumnType("timestamptz");
            entity.Property(e => e.SnapshotStatus).HasColumnName("snapshot_status");
            entity.Property(e => e.PolicyInForceAtLoss).HasColumnName("policy_in_force_at_loss");
            entity.Property(e => e.PolicyStatusAtLoss).HasColumnName("policy_status_at_loss");
            entity.Property(e => e.SnapshotCoverageCodes).HasColumnName("snapshot_coverage_codes");
            entity.Property(e => e.ProductCode).HasColumnName("product_code");
            entity.Property(e => e.ProductVersion).HasColumnName("product_version");
            entity.Property(e => e.LineOfBusiness).HasColumnName("line_of_business");
            entity.Property(e => e.LossAt).HasColumnName("loss_at").HasColumnType("timestamptz");
            entity.Property(e => e.LossDate).HasColumnName("loss_date");
            entity.Property(e => e.NoticeOn).HasColumnName("notice_on");
            entity.Property(e => e.LossCause).HasColumnName("loss_cause");
            entity.Property(e => e.LossLocationEncrypted).HasColumnName("loss_location_encrypted");
            entity.Property(e => e.DescriptionEncrypted).HasColumnName("description_encrypted");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.ReceiptMedium).HasColumnName("receipt_medium");
            entity.Property(e => e.HandlingSegment).HasColumnName("handling_segment");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.SubStatus).HasColumnName("sub_status");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.CloseReasonCode).HasColumnName("close_reason_code");
            entity.Property(e => e.CoverageInQuestion).HasColumnName("coverage_in_question");
            entity.Property(e => e.DuplicateOfClaimId).HasColumnName("duplicate_of_claim_id");
            entity.Property(e => e.DuplicateReasonCode).HasColumnName("duplicate_reason_code");
            entity.Property(e => e.Handler).HasColumnName("handler");
            entity.Property(e => e.ReopenCount).HasColumnName("reopen_count");
            entity.Property(e => e.LastExposureSequence).HasColumnName("last_exposure_sequence");
            entity.Property(e => e.LastTransactionSequence).HasColumnName("last_transaction_sequence");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.ClaimNumber }).IsUnique().HasDatabaseName("ux_claim_number");
            entity.HasIndex(e => new { e.LegalEntityId, e.PolicyId, e.LossDate }).HasDatabaseName("ix_claim_policy_loss_date");
            entity.HasIndex(e => new { e.LegalEntityId, e.PolicyNumber }).HasDatabaseName("ix_claim_policy_number");
            entity.HasIndex(e => new { e.LegalEntityId, e.InsuredPartyId }).HasDatabaseName("ix_claim_insured_party");
            entity.HasOne<ClaimRow>().WithMany().HasForeignKey(e => e.DuplicateOfClaimId).HasConstraintName("fk_claim_duplicate_of").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FnolSnapshotRow>(entity =>
        {
            entity.ToTable("fnol_snapshot", table => CommonChecks(table, "fnol_snapshot"));
            entity.HasKey(e => e.FnolId).HasName("pk_fnol_snapshot");
            entity.Property(e => e.FnolId).HasColumnName("fnol_id");
            MapCommon(entity);
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.ReporterPartyId).HasColumnName("reporter_party_id");
            entity.Property(e => e.PayloadEncrypted).HasColumnName("payload_encrypted");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.ClaimId).IsUnique().HasDatabaseName("ux_fnol_snapshot_claim");
            entity.HasOne<ClaimRow>().WithMany().HasForeignKey(e => e.ClaimId).HasConstraintName("fk_fnol_snapshot_claim").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ClaimantRow>(entity =>
        {
            entity.ToTable("claimant", table =>
            {
                CommonChecks(table, "claimant");
                table.HasCheckConstraint("ck_claimant_type", Codes.CheckSql<ClaimantType>("claimant_type"));
            });
            entity.HasKey(e => e.ClaimantId).HasName("pk_claimant");
            entity.Property(e => e.ClaimantId).HasColumnName("claimant_id");
            MapCommon(entity);
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.ClaimantType).HasColumnName("claimant_type");
            entity.HasIndex(e => new { e.ClaimId, e.PartyId }).IsUnique().HasDatabaseName("ux_claimant_claim_party");
            entity.HasIndex(e => new { e.LegalEntityId, e.PartyId }).HasDatabaseName("ix_claimant_party");
            entity.HasOne<ClaimRow>().WithMany().HasForeignKey(e => e.ClaimId).HasConstraintName("fk_claimant_claim").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IncidentRow>(entity =>
        {
            entity.ToTable("incident", table =>
            {
                CommonChecks(table, "incident");
                table.HasCheckConstraint("ck_incident_type", "incident_type IN ('VEHICLE', 'PROPERTY', 'INJURY', 'LIABILITY')");
            });
            entity.HasKey(e => e.IncidentId).HasName("pk_incident");
            entity.Property(e => e.IncidentId).HasColumnName("incident_id");
            MapCommon(entity);
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.IncidentType).HasColumnName("incident_type");
            entity.Property(e => e.VehicleRef).HasColumnName("vehicle_ref");
            entity.Property(e => e.Drivable).HasColumnName("drivable");
            entity.Property(e => e.DamageAreas).HasColumnName("damage_areas");
            entity.HasIndex(e => e.ClaimId).HasDatabaseName("ix_incident_claim");
            entity.HasOne<ClaimRow>().WithMany().HasForeignKey(e => e.ClaimId).HasConstraintName("fk_incident_claim").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExposureRow>(entity =>
        {
            entity.ToTable("exposure", table =>
            {
                CommonChecks(table, "exposure");
                table.HasCheckConstraint("ck_exposure_status", "status IN ('OPEN', 'CLOSED')");
                table.HasCheckConstraint("ck_exposure_sub_status", "sub_status IS NULL OR sub_status IN ('NEW', 'IN_PROGRESS')");
                table.HasCheckConstraint("ck_exposure_open_shape", "status <> 'OPEN' OR (sub_status IS NOT NULL AND outcome IS NULL AND closed_at IS NULL)");
                table.HasCheckConstraint("ck_exposure_closed_shape", "status <> 'CLOSED' OR (sub_status IS NULL AND outcome IS NOT NULL AND closed_at IS NOT NULL)");
                table.HasCheckConstraint("ck_exposure_outcome", "outcome IS NULL OR " + Codes.CheckSql<ClaimOutcomeCode>("outcome"));
                table.HasCheckConstraint("ck_exposure_indication", Codes.CheckSql<CoverageIndicationCode>("coverage_indication"));
                table.HasCheckConstraint("ck_exposure_decision", Codes.CheckSql<CoverageDecisionCode>("coverage_decision"));
                table.HasCheckConstraint("ck_exposure_sequence", "sequence >= 1");
            });
            entity.HasKey(e => e.ExposureId).HasName("pk_exposure");
            entity.Property(e => e.ExposureId).HasColumnName("exposure_id");
            MapCommon(entity);
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.ExposureNumber).HasColumnName("exposure_number");
            entity.Property(e => e.Sequence).HasColumnName("sequence");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.CoverageCode).HasColumnName("coverage_code");
            entity.Property(e => e.ClaimantId).HasColumnName("claimant_id");
            entity.Property(e => e.IncidentId).HasColumnName("incident_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.SubStatus).HasColumnName("sub_status");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.CoverageIndication).HasColumnName("coverage_indication");
            entity.Property(e => e.CoverageDecision).HasColumnName("coverage_decision");
            entity.Property(e => e.DuplicateReason).HasColumnName("duplicate_reason");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.ClaimId, e.Sequence }).IsUnique().HasDatabaseName("ux_exposure_claim_sequence");
            entity.HasIndex(e => new { e.LegalEntityId, e.ExposureNumber }).IsUnique().HasDatabaseName("ux_exposure_number");

            // REQ-CLM-063: one open exposure per (coverage, claimant, incident) unless a reason is recorded; a race loses with CLM-ERR-EXPOSURE-DUPLICATE.
            entity.HasIndex(e => new { e.ClaimId, e.CoverageCode, e.ClaimantId, e.IncidentId }).IsUnique().AreNullsDistinct(false)
                .HasFilter("status = 'OPEN' AND duplicate_reason IS NULL").HasDatabaseName("ux_exposure_open_coverage_claimant_incident");
            entity.HasOne<ClaimRow>().WithMany().HasForeignKey(e => e.ClaimId).HasConstraintName("fk_exposure_claim").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ClaimantRow>().WithMany().HasForeignKey(e => e.ClaimantId).HasConstraintName("fk_exposure_claimant").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncidentRow>().WithMany().HasForeignKey(e => e.IncidentId).HasConstraintName("fk_exposure_incident").OnDelete(DeleteBehavior.Restrict);
        });

        MapFinancials(modelBuilder);
        ReverificationModel.Map(modelBuilder);
    }

    private static void MapFinancials(ModelBuilder modelBuilder) => ClaimsFinancialModel.Map(modelBuilder);

    internal static void CommonChecks(TableBuilder table, string name)
    {
        table.HasCheckConstraint($"ck_{name}_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
        table.HasCheckConstraint($"ck_{name}_record_version", "record_version >= 1");
    }

    internal static void MapCommon<T>(EntityTypeBuilder<T> entity)
        where T : ClaimsRow
    {
        entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
        entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        entity.Property(e => e.CreatedBy).HasColumnName("created_by");
        entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Claims</c> (no connection opened).</summary>
internal sealed class ClaimsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ClaimsDbContext>
{
    public ClaimsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<ClaimsDbContext>("Host=localhost;Database=coreins_design;Username=design", ClaimsModule.Schema));
}
