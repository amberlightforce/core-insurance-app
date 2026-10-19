using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Documents.Persistence;

/// <summary>The Documents module's EF Core context (schema <c>doc</c>): documents, deliveries, delivery evidence and archived content.</summary>
internal sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : ModuleDbContext(options)
{
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();

    public DbSet<DeliveryRow> Deliveries => Set<DeliveryRow>();

    public DbSet<EvidenceRow> Evidence => Set<EvidenceRow>();

    public DbSet<ArchiveRow> Archives => Set<ArchiveRow>();

    protected override string Schema => DocumentsModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.MapDsarRestrictions();

        modelBuilder.Entity<DocumentRow>(entity =>
        {
            entity.ToTable("document", t => t.HasCheckConstraint("ck_document_status", "status IN ('Requested','Rendering','Rendered','Failed','Superseded')"));
            entity.HasKey(e => e.DocumentId).HasName("pk_document");
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.DocumentNumber).HasColumnName("document_number");
            entity.Property(e => e.TemplateKey).HasColumnName("template_key");
            entity.Property(e => e.RequestNo).HasColumnName("request_no");
            entity.Property(e => e.SubjectModule).HasColumnName("subject_module");
            entity.Property(e => e.SubjectType).HasColumnName("subject_type");
            entity.Property(e => e.SubjectId).HasColumnName("subject_id");
            entity.Property(e => e.RecipientPartyId).HasColumnName("recipient_party_id");
            entity.Property(e => e.Language).HasColumnName("language").HasColumnType("char(2)");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Medium).HasColumnName("medium");
            entity.Property(e => e.ArchiveId).HasColumnName("archive_id");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.RenderedAt).HasColumnName("rendered_at").HasColumnType("timestamptz");

            // De-duplication of a request (REQ-DOC RT-11): one document per template and request number.
            entity.HasIndex(e => new { e.LegalEntityId, e.TemplateKey, e.RequestNo }).IsUnique().HasDatabaseName("ux_document_request");
            entity.HasIndex(e => new { e.LegalEntityId, e.DocumentNumber }).IsUnique().HasDatabaseName("ux_document_number");
            entity.HasIndex(e => new { e.SubjectModule, e.SubjectType, e.SubjectId }).HasDatabaseName("ix_document_subject");
        });

        modelBuilder.Entity<DeliveryRow>(entity =>
        {
            entity.ToTable("delivery", t =>
            {
                t.HasCheckConstraint("ck_delivery_status", "status IN ('Pending','Sent','Delivered','Failed','Bounced')");
                t.HasCheckConstraint("ck_delivery_delivered_date", "status <> 'Delivered' OR notification_date IS NOT NULL");
            });
            entity.HasKey(e => e.DeliveryId).HasName("pk_delivery");
            entity.Property(e => e.DeliveryId).HasColumnName("delivery_id");
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.RecipientPartyId).HasColumnName("recipient_party_id");
            entity.Property(e => e.RecipientRole).HasColumnName("recipient_role");
            entity.Property(e => e.Channel).HasColumnName("channel");
            entity.Property(e => e.Medium).HasColumnName("medium");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ProofLevel).HasColumnName("proof_level");
            entity.Property(e => e.NotificationDate).HasColumnName("notification_date");
            entity.Property(e => e.FailureReason).HasColumnName("failure_reason");
            entity.Property(e => e.IsReprint).HasColumnName("is_reprint");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.DocumentId).HasDatabaseName("ix_delivery_document");
        });

        modelBuilder.Entity<EvidenceRow>(entity =>
        {
            entity.ToTable("evidence");
            entity.HasKey(e => e.EvidenceId).HasName("pk_evidence");
            entity.Property(e => e.EvidenceId).HasColumnName("evidence_id");
            entity.Property(e => e.DeliveryId).HasColumnName("delivery_id");
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.ProviderEventId).HasColumnName("provider_event_id");
            entity.Property(e => e.EventType).HasColumnName("event_type");
            entity.Property(e => e.ProofDate).HasColumnName("proof_date");
            entity.Property(e => e.ProofReference).HasColumnName("proof_reference");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            // The provider event id is the idempotency key of a callback.
            entity.HasIndex(e => new { e.Provider, e.ProviderEventId }).IsUnique().HasDatabaseName("ux_evidence_provider_event");
            entity.HasIndex(e => e.DocumentId).HasDatabaseName("ix_evidence_document");
        });

        modelBuilder.Entity<ArchiveRow>(entity =>
        {
            entity.ToTable("archive", t => t.HasCheckConstraint("ck_archive_hash", "content_hash ~ '^[0-9a-f]{64}$'"));
            entity.HasKey(e => e.ArchiveId).HasName("pk_archive");
            entity.Property(e => e.ArchiveId).HasColumnName("archive_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ContentType).HasColumnName("content_type");
            entity.Property(e => e.Content).HasColumnName("content").HasColumnType("bytea");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });
    }
}

internal sealed class DocumentRow
{
    public DocumentId DocumentId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;

    public string TemplateKey { get; set; } = string.Empty;

    public string RequestNo { get; set; } = string.Empty;

    public string SubjectModule { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string SubjectId { get; set; } = string.Empty;

    public PartyId RecipientPartyId { get; set; }

    public string Language { get; set; } = "el";

    public string Status { get; set; } = string.Empty;

    public string Medium { get; set; } = string.Empty;

    public Guid ArchiveId { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant? RenderedAt { get; set; }
}

internal sealed class DeliveryRow
{
    public DeliveryId DeliveryId { get; set; }

    public DocumentId DocumentId { get; set; }

    public PartyId RecipientPartyId { get; set; }

    public string RecipientRole { get; set; } = "ADDRESSEE";

    public string Channel { get; set; } = string.Empty;

    public string Medium { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? ProofLevel { get; set; }

    /// <summary>The date delivery was proven. Null until a provider proof exists: no proof, no notification date.</summary>
    public BusinessDate? NotificationDate { get; set; }

    public string? FailureReason { get; set; }

    public bool IsReprint { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}

internal sealed class EvidenceRow
{
    public Guid EvidenceId { get; set; }

    public DeliveryId DeliveryId { get; set; }

    public DocumentId DocumentId { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string ProviderEventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public BusinessDate? ProofDate { get; set; }

    public string? ProofReference { get; set; }

    /// <summary>What DOC did with the callback: APPLIED, IGNORED_TERMINAL or REJECTED_NO_PROOF.</summary>
    public string Outcome { get; set; } = string.Empty;

    public Instant ReceivedAt { get; set; }
}

internal sealed class ArchiveRow
{
    public Guid ArchiveId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];

    public string ContentHash { get; set; } = string.Empty;

    public string Metadata { get; set; } = "{}";

    public Instant CreatedAt { get; set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> on this project.</summary>
internal sealed class DocumentsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<DocumentsDbContext>("Host=localhost;Database=coreins_design;Username=design", DocumentsModule.Schema));
}
