using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Mapping;

/// <summary>Status of a mapping spec version.</summary>
internal static class MappingStatus
{
    public const string Draft = "DRAFT";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

/// <summary>
/// <c>mig.mapping_spec</c>: one version of a field mapping from a staged legacy object type to a module importer payload (BO-MIG-04). The content
/// (name, version, types, fields, hash) never changes; only the approval state moves DRAFT to PENDING_APPROVAL to APPROVED or REJECTED (a database trigger enforces it).
/// </summary>
internal sealed class MappingSpecRow
{
    public Guid SpecId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Version { get; set; }

    public string SourceObjectType { get; set; } = string.Empty;

    public string TargetModule { get; set; } = string.Empty;

    public string TargetObjectType { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The field mappings, as JSON.</summary>
    public string Fields { get; set; } = "[]";

    /// <summary>SHA-256 of the canonical fields and code tables; the approval covers it.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public string Status { get; set; } = MappingStatus.Draft;

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public string? RequestedBy { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecisionComment { get; set; }
}

/// <summary><c>mig.code_map</c>: one code of a code table of a spec version (legacy code to core code). Insert-only; the table is versioned with its spec.</summary>
internal sealed class CodeMapRow
{
    public Guid EntryId { get; set; }

    public Guid SpecId { get; set; }

    public string MapName { get; set; } = string.Empty;

    public string LegacyValue { get; set; } = string.Empty;

    public string CoreValue { get; set; } = string.Empty;
}

internal static class MappingModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<MappingSpecRow>(e =>
        {
            e.ToTable("mapping_spec", t => t.HasCheckConstraint("ck_mapping_spec_status", "status IN ('DRAFT','PENDING_APPROVAL','APPROVED','REJECTED')"));
            e.HasKey(r => r.SpecId).HasName("pk_mapping_spec");
            e.Property(r => r.SpecId).HasColumnName("spec_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.Name).HasColumnName("name");
            e.Property(r => r.Version).HasColumnName("version");
            e.Property(r => r.SourceObjectType).HasColumnName("source_object_type");
            e.Property(r => r.TargetModule).HasColumnName("target_module");
            e.Property(r => r.TargetObjectType).HasColumnName("target_object_type");
            e.Property(r => r.Description).HasColumnName("description");
            e.Property(r => r.Fields).HasColumnName("fields").HasColumnType("jsonb");
            e.Property(r => r.ContentHash).HasColumnName("content_hash");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.CreatedBy).HasColumnName("created_by");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(r => r.RequestedBy).HasColumnName("requested_by");
            e.Property(r => r.ApprovalRequestId).HasColumnName("approval_request_id");
            e.Property(r => r.DecidedBy).HasColumnName("decided_by");
            e.Property(r => r.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            e.Property(r => r.DecisionComment).HasColumnName("decision_comment");
            e.HasIndex(r => new { r.LegalEntityId, r.Name, r.Version }).IsUnique().HasDatabaseName("ux_mapping_spec_name_version");
        });

        model.Entity<CodeMapRow>(e =>
        {
            e.ToTable("code_map");
            e.HasKey(r => r.EntryId).HasName("pk_code_map");
            e.Property(r => r.EntryId).HasColumnName("entry_id");
            e.Property(r => r.SpecId).HasColumnName("spec_id");
            e.Property(r => r.MapName).HasColumnName("map_name");
            e.Property(r => r.LegacyValue).HasColumnName("legacy_value");
            e.Property(r => r.CoreValue).HasColumnName("core_value");
            e.HasIndex(r => new { r.SpecId, r.MapName, r.LegacyValue }).IsUnique().HasDatabaseName("ux_code_map_entry");
            e.HasOne<MappingSpecRow>().WithMany().HasForeignKey(r => r.SpecId).HasConstraintName("fk_code_map_spec");
        });
    }
}
