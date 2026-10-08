using CoreIns.Modules.Compliance.Domain;
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

    protected override string Schema => ComplianceModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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

/// <summary>The last number CMP issued in a fiscal series (gapless under a row lock in the caller's transaction).</summary>
internal sealed class FiscalSeriesRow
{
    public LegalEntityId LegalEntityId { get; set; }

    public string SeriesId { get; set; } = string.Empty;

    public long LastNumber { get; set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add … --project src/CoreIns.Modules.Compliance</c>.</summary>
internal sealed class ComplianceDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ComplianceDbContext>
{
    public ComplianceDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<ComplianceDbContext>("Host=localhost;Database=coreins_design;Username=design", ComplianceModule.Schema));
}
