using CoreIns.Modules.Compliance.AiRegister;
using CoreIns.Modules.Compliance.Complaints;
using CoreIns.Modules.Compliance.Domain;
using CoreIns.Modules.Compliance.Dsar;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Compliance.Persistence;

/// <summary>
/// The Compliance module's EF Core context (schema <c>cmp</c>). SL-BIL builds only the fiscal-document stub path
/// (W5-CMP-01 subset): fiscal documents and the per-series counters CMP issues numbers from (D3, REQ-CMP-038).
/// </summary>
internal sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : ModuleDbContext(options)
{
    public DbSet<FiscalDocumentRow> FiscalDocuments => Set<FiscalDocumentRow>();

    public DbSet<FiscalSeriesRow> FiscalSeries => Set<FiscalSeriesRow>();

    internal DbSet<AiSystemRow> AiSystems => Set<AiSystemRow>();
    public DbSet<ClockRow> Clocks => Set<ClockRow>();

    public DbSet<ClockEventRow> ClockEvents => Set<ClockEventRow>();

    public DbSet<BureauHandoverRow> BureauHandovers => Set<BureauHandoverRow>();

    public DbSet<LegacyFiscalMarkRow> LegacyFiscalMarks => Set<LegacyFiscalMarkRow>();

    protected override string Schema => ComplianceModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.MapDsarRestrictions();
        modelBuilder.MapDsar();
        modelBuilder.MapComplaints();

        modelBuilder.Entity<FiscalDocumentRow>(entity =>
        {
            entity.ToTable("fiscal_document", table =>
            {
                table.HasCheckConstraint("ck_fiscal_document_status", Domain.FiscalDocuments.CheckSql<FiscalDocumentStatus>("status"));
                table.HasCheckConstraint("ck_fiscal_document_role", Domain.FiscalDocuments.CheckSql<FiscalDocumentRole>("role"));
                table.HasCheckConstraint("ck_fiscal_document_revision", "revision >= 0");
                table.HasCheckConstraint("ck_fiscal_document_currency", "currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_fiscal_document_registered", "status <> 'REGISTERED' OR (mark IS NOT NULL AND uid IS NOT NULL)");
                table.HasCheckConstraint("ck_fiscal_document_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.FiscalDocumentId).HasName("pk_fiscal_document");
            entity.Property(e => e.FiscalDocumentId).HasColumnName("fiscal_document_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.SourceType).HasColumnName("source_type");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.Role).HasColumnName("role");
            entity.Property(e => e.Revision).HasColumnName("revision");
            entity.Property(e => e.CorrelatedDocumentId).HasColumnName("correlated_document_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.DocumentType).HasColumnName("document_type");
            entity.Property(e => e.DocumentTypeIsPlaceholder).HasColumnName("document_type_is_placeholder");
            entity.Property(e => e.IssueDate).HasColumnName("issue_date");
            entity.Property(e => e.CounterpartyPartyId).HasColumnName("counterparty_party_id");
            entity.Property(e => e.Total).HasColumnName("total").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Lines).HasColumnName("lines").HasColumnType("jsonb");
            entity.Property(e => e.Series).HasColumnName("series");
            entity.Property(e => e.Number).HasColumnName("number");
            entity.Property(e => e.Mark).HasColumnName("mark");
            entity.Property(e => e.Uid).HasColumnName("uid");
            entity.Property(e => e.QrPayloadRef).HasColumnName("qr_payload_ref");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.Stub).HasColumnName("stub");
            entity.Property(e => e.RejectionCodes).HasColumnName("rejection_codes");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RegisteredAt).HasColumnName("registered_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();

            // REQ-CMP-031: the fiscal idempotency key is source type + source id + role + revision.
            entity.HasIndex(e => new { e.LegalEntityId, e.SourceType, e.SourceId, e.Role, e.Revision }).IsUnique().HasDatabaseName("ux_fiscal_document_source");
            entity.HasIndex(e => new { e.LegalEntityId, e.Series, e.Number }).IsUnique().HasDatabaseName("ux_fiscal_document_number");
        });

        modelBuilder.Entity<AiSystemRow>(entity =>
        {
            entity.ToTable("ai_system", table =>
            {
                table.HasCheckConstraint("ck_ai_system_status", "status IN ('DRAFT','IN_REVIEW','APPROVED','SUSPENDED','RETIRED')");
                table.HasCheckConstraint("ck_ai_system_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.FeatureId).HasName("pk_ai_system");
            entity.Property(e => e.FeatureId).HasColumnName("feature_id").HasMaxLength(32);
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(e => e.AiActClass).HasColumnName("ai_act_class").HasMaxLength(64);
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(16);
            entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(512);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
        });

        modelBuilder.Entity<ClockRow>(entity =>
        {
            entity.ToTable("clock", table =>
            {
                table.HasCheckConstraint("ck_clock_status", "status IN ('RUNNING','WARNED','PAUSED','MET','BREACHED','ELAPSED','CANCELLED')");
                table.HasCheckConstraint("ck_clock_kind", "kind IN ('WAITING_PERIOD','DEADLINE')");
                table.HasCheckConstraint("ck_clock_value_version", "value_version >= 1");
                table.HasCheckConstraint("ck_clock_record_version", "record_version >= 1");
            });
            entity.HasKey(e => e.ClockId).HasName("pk_clock");
            entity.Property(e => e.ClockId).HasColumnName("clock_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.ClockCode).HasColumnName("clock_code");
            entity.Property(e => e.SubjectModule).HasColumnName("subject_module");
            entity.Property(e => e.SubjectType).HasColumnName("subject_type");
            entity.Property(e => e.SubjectId).HasColumnName("subject_id");
            entity.Property(e => e.Key).HasColumnName("key");
            entity.Property(e => e.Variant).HasColumnName("variant");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            entity.Property(e => e.Deadline).HasColumnName("deadline").HasColumnType("timestamptz");
            entity.Property(e => e.WarnAt).HasColumnName("warn_at").HasColumnType("timestamptz");
            entity.Property(e => e.PausedAt).HasColumnName("paused_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.MetLate).HasColumnName("met_late");
            entity.Property(e => e.ValueVersion).HasColumnName("value_version");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();

            // Idempotent start: one instance per definition, subject and key.
            entity.HasIndex(e => new { e.LegalEntityId, e.ClockCode, e.SubjectModule, e.SubjectType, e.SubjectId, e.Key }).IsUnique().HasDatabaseName("ux_clock_instance");
            // The sweeper's due scan.
            entity.HasIndex(e => new { e.Status, e.Deadline }).HasDatabaseName("ix_clock_status_deadline");
            entity.HasIndex(e => new { e.SubjectModule, e.SubjectType, e.SubjectId }).HasDatabaseName("ix_clock_subject");
        });

        modelBuilder.Entity<ClockEventRow>(entity =>
        {
            entity.ToTable("clock_event");
            entity.HasKey(e => e.ClockEventId).HasName("pk_clock_event");
            entity.Property(e => e.ClockEventId).HasColumnName("clock_event_id");
            entity.Property(e => e.ClockId).HasColumnName("clock_id");
            entity.Property(e => e.Type).HasColumnName("type");
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            entity.Property(e => e.Actor).HasColumnName("actor");
            entity.Property(e => e.Detail).HasColumnName("detail").HasColumnType("jsonb");
            entity.HasIndex(e => new { e.ClockId, e.OccurredAt }).HasDatabaseName("ix_clock_event_clock");
            entity.HasOne<ClockRow>().WithMany().HasForeignKey(e => e.ClockId).HasConstraintName("fk_clock_event_clock");
        });

        modelBuilder.Entity<BureauHandoverRow>(entity =>
        {
            entity.ToTable("bureau_handover");
            entity.HasKey(e => e.HandoverId).HasName("pk_bureau_handover");
            entity.Property(e => e.HandoverId).HasColumnName("handover_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.VehicleRef).HasColumnName("vehicle_ref");
            entity.Property(e => e.PolicyId).HasColumnName("policy_id");
            entity.Property(e => e.HandoverDate).HasColumnName("handover_date");
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.VehicleRef }).IsUnique().HasDatabaseName("ux_bureau_handover_vehicle");
        });

        modelBuilder.Entity<LegacyFiscalMarkRow>(entity =>
        {
            entity.ToTable("legacy_fiscal_mark");
            entity.HasKey(e => e.MarkId).HasName("pk_legacy_fiscal_mark");
            entity.Property(e => e.MarkId).HasColumnName("mark_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.SeriesId).HasColumnName("series_id");
            entity.Property(e => e.LegacyNumber).HasColumnName("legacy_number");
            entity.Property(e => e.Mark).HasColumnName("mark");
            entity.Property(e => e.BatchId).HasColumnName("batch_id");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.LegalEntityId, e.SeriesId, e.LegacyNumber }).IsUnique().HasDatabaseName("ux_legacy_fiscal_mark_number");
            entity.HasIndex(e => new { e.LegalEntityId, e.Mark }).HasDatabaseName("ix_legacy_fiscal_mark_mark");
        });

        modelBuilder.Entity<FiscalSeriesRow>(entity =>
        {
            entity.ToTable("fiscal_series", table => table.HasCheckConstraint("ck_fiscal_series_last", "last_number >= 0"));
            entity.HasKey(e => new { e.LegalEntityId, e.SeriesId }).HasName("pk_fiscal_series");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.SeriesId).HasColumnName("series_id");
            entity.Property(e => e.LastNumber).HasColumnName("last_number");
        });
    }
}

/// <summary>A fiscal document (REQ-CMP-030, REQ-CMP-038, REQ-CMP-046).</summary>
internal sealed class FiscalDocumentRow
{
    public FiscalDocumentId FiscalDocumentId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public string SourceType { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public int Revision { get; set; }

    /// <summary>For a CREDIT: the fiscal document of the original it corrects (null = uncorrelated credit).</summary>
    public Guid? CorrelatedDocumentId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public bool DocumentTypeIsPlaceholder { get; set; }

    public BusinessDate IssueDate { get; set; }

    public PartyId CounterpartyPartyId { get; set; }

    public decimal Total { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>Lines as JSON (fiscal-category key, amount, charge id).</summary>
    public string Lines { get; set; } = "[]";

    public string? Series { get; set; }

    public string? Number { get; set; }

    public string? Mark { get; set; }

    public string? Uid { get; set; }

    public string? QrPayloadRef { get; set; }

    public string Channel { get; set; } = string.Empty;

    public bool Stub { get; set; }

    public string[] RejectionCodes { get; set; } = [];

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant? RegisteredAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary>The bureau handover date recorded for a migrated vehicle (record only, no transmission; SL6-MIG-CLM-RI-IMPORT).</summary>
internal sealed class BureauHandoverRow
{
    public Guid HandoverId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    /// <summary>Normalised plate (the vehicle reference).</summary>
    public string VehicleRef { get; set; } = string.Empty;

    public Guid? PolicyId { get; set; }

    public BusinessDate HandoverDate { get; set; }

    public Guid BatchId { get; set; }

    public Instant RecordedAt { get; set; }
}

/// <summary>A legacy fiscal document mark kept so a credit note can still look up the original (SL6-MIG-CLM-RI-IMPORT).</summary>
internal sealed class LegacyFiscalMarkRow
{
    public Guid MarkId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string SeriesId { get; set; } = string.Empty;

    public long LegacyNumber { get; set; }

    public string Mark { get; set; } = string.Empty;

    public Guid BatchId { get; set; }

    public Instant RecordedAt { get; set; }
}

/// <summary>The last number CMP issued in a fiscal series (gapless under a row lock in the caller's transaction).</summary>
internal sealed class FiscalSeriesRow
{
    public LegalEntityId LegalEntityId { get; set; }

    public string SeriesId { get; set; } = string.Empty;

    public long LastNumber { get; set; }
}

/// <summary>One running or finished clock (REQ-CMP-095..104): a definition applied to a subject.</summary>
internal sealed class ClockRow
{
    public StatutoryClockId ClockId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    public string ClockCode { get; set; } = string.Empty;

    public string SubjectModule { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    /// <summary>Instance key within definition and subject; empty by default.</summary>
    public string Key { get; set; } = string.Empty;

    public string? Variant { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public Instant StartedAt { get; set; }

    /// <summary>Current due instant; moves on resume and extend.</summary>
    public Instant Deadline { get; set; }

    /// <summary>When the warning is due (null: none or already given).</summary>
    public Instant? WarnAt { get; set; }

    public Instant? PausedAt { get; set; }

    public Instant? ClosedAt { get; set; }

    public string? Outcome { get; set; }

    public bool MetLate { get; set; }

    public int ValueVersion { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public int RecordVersion { get; set; }
}

/// <summary>The history of a clock: every transition, with its business time.</summary>
internal sealed class ClockEventRow
{
    public Guid ClockEventId { get; set; }

    public StatutoryClockId ClockId { get; set; }

    public string Type { get; set; } = string.Empty;

    public Instant OccurredAt { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string Detail { get; set; } = "{}";
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Compliance</c>.</summary>
internal sealed class ComplianceDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ComplianceDbContext>
{
    public ComplianceDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<ComplianceDbContext>("Host=localhost;Database=coreins_design;Username=design", ComplianceModule.Schema));
}
