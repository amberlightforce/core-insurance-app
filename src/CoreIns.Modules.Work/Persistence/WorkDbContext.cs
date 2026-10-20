using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Work.Persistence;

/// <summary>
/// The Work module's EF Core context (schema <c>wrk</c>): activities (tasks with assignment and SLA clock) and the DSAR restriction table.
/// </summary>
internal sealed class WorkDbContext(DbContextOptions<WorkDbContext> options) : ModuleDbContext(options)
{
    public DbSet<ActivityRow> Activities => Set<ActivityRow>();

    protected override string Schema => WorkModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.MapDsarRestrictions();

        modelBuilder.Entity<ActivityRow>(entity =>
        {
            entity.ToTable("activity", table =>
            {
                table.HasCheckConstraint("ck_activity_state", "state IN ('OPEN', 'COMPLETED', 'SKIPPED', 'CANCELLED')");
                table.HasCheckConstraint("ck_activity_assignee_type", "assignee_type IN ('USER', 'GROUP', 'QUEUE')");
                table.HasCheckConstraint("ck_activity_closed", "(state = 'OPEN') = (closed_at IS NULL)");
            });
            entity.HasKey(e => e.ActivityId).HasName("pk_activity");
            entity.Property(e => e.ActivityId).HasColumnName("activity_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Number).HasColumnName("number").HasMaxLength(64);
            entity.Property(e => e.PatternCode).HasColumnName("pattern_code");
            entity.Property(e => e.PatternVersion).HasColumnName("pattern_version");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.Category).HasColumnName("category");
            entity.Property(e => e.Priority).HasColumnName("priority");
            entity.Property(e => e.SlaPolicy).HasColumnName("sla_policy");
            entity.Property(e => e.GateBlocking).HasColumnName("gate_blocking");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.PrimaryModule).HasColumnName("primary_module");
            entity.Property(e => e.PrimaryType).HasColumnName("primary_type");
            entity.Property(e => e.PrimaryId).HasColumnName("primary_id");
            entity.Property(e => e.SecondaryRefs).HasColumnName("secondary_refs").HasColumnType("jsonb");
            entity.Property(e => e.SubjectPartyId).HasColumnName("subject_party_id");
            entity.Property(e => e.RaisingModule).HasColumnName("raising_module");
            entity.Property(e => e.CauseRef).HasColumnName("cause_ref");
            entity.Property(e => e.AssigneeType).HasColumnName("assignee_type");
            entity.Property(e => e.AssigneeId).HasColumnName("assignee_id");
            entity.Property(e => e.QueueCode).HasColumnName("queue_code");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.TargetAt).HasColumnName("target_at").HasColumnType("timestamptz");
            entity.Property(e => e.EscalationAt).HasColumnName("escalation_at").HasColumnType("timestamptz");
            entity.Property(e => e.BreachedAt).HasColumnName("breached_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClosedBy).HasColumnName("closed_by");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.Note).HasColumnName("note");
            entity.Property(e => e.CloseReason).HasColumnName("close_reason");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => e.Number).IsUnique().HasDatabaseName("ux_activity_number");
            // One open activity per pattern, primary object and cause (WRK-ERR-DUPLICATE); closing frees the key.
            entity.HasIndex(e => new { e.LegalEntityId, e.PatternCode, e.PrimaryModule, e.PrimaryType, e.PrimaryId, e.CauseRef })
                .IsUnique().HasFilter("state = 'OPEN'").HasDatabaseName("ux_activity_open_duplicate");
            entity.HasIndex(e => new { e.LegalEntityId, e.State, e.AssigneeType, e.AssigneeId, e.TargetAt }).HasDatabaseName("ix_activity_inbox");
            entity.HasIndex(e => new { e.LegalEntityId, e.PrimaryModule, e.PrimaryType, e.PrimaryId }).HasDatabaseName("ix_activity_primary_object");
            entity.HasIndex(e => e.SubjectPartyId).HasDatabaseName("ix_activity_subject_party");
            entity.HasIndex(e => e.TargetAt).HasFilter("state = 'OPEN' AND breached_at IS NULL").HasDatabaseName("ix_activity_sla_sweep");
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations</c>.</summary>
internal sealed class WorkDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WorkDbContext>
{
    public WorkDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<WorkDbContext>("Host=localhost;Database=coreins_design;Username=design", WorkModule.Schema));
}
