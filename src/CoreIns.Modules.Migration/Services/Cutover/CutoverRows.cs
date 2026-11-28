using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Cutover;

/// <summary>Cut-over plan lifecycle (BO-MIG-12).</summary>
internal static class CutoverStatus
{
    public const string Planned = "PLANNED";
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
    public const string RolledBack = "ROLLED_BACK";
    public const string RollbackBlocked = "ROLLBACK_BLOCKED";
}

internal static class CutoverKind
{
    public const string Rehearsal = "REHEARSAL";
    public const string Cutover = "CUTOVER";
}

internal static class StepKind
{
    public const string GoNoGo = "GO_NO_GO";
    public const string BatchRun = "BATCH_RUN";
}

internal static class StepStatus
{
    public const string Pending = "PENDING";
    public const string InProgress = "IN_PROGRESS";
    public const string Done = "DONE";
    public const string RolledBack = "ROLLED_BACK";
}

/// <summary><c>mig.cutover_plan</c>: a rehearsal (synthetic data) or the real cut-over, an ordered runbook of steps.</summary>
internal sealed class CutoverPlanRow
{
    public Guid PlanId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public string Status { get; set; } = CutoverStatus.Planned;

    public string CreatedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    /// <summary>When the rollback was last previewed (dry run); the real rollback needs it.</summary>
    public Instant? RollbackPreviewedAt { get; set; }

    public Instant? RolledBackAt { get; set; }

    public int RecordVersion { get; set; }
}

/// <summary><c>mig.cutover_step</c>: one step of the runbook with its stored timing.</summary>
internal sealed class CutoverStepRow
{
    public Guid StepId { get; set; }

    public Guid PlanId { get; set; }

    public int Seq { get; set; }

    public string Kind { get; set; } = string.Empty;

    public Guid? BatchId { get; set; }

    public string Status { get; set; } = StepStatus.Pending;

    public Instant? StartedAt { get; set; }

    public Instant? CompletedAt { get; set; }

    public long? DurationMs { get; set; }

    public string? Result { get; set; }

    public string? Note { get; set; }
}

internal static class CutoverModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<CutoverPlanRow>(e =>
        {
            e.ToTable("cutover_plan", t =>
            {
                t.HasCheckConstraint("ck_cutover_plan_kind", "kind IN ('REHEARSAL','CUTOVER')");
                t.HasCheckConstraint("ck_cutover_plan_status", "status IN ('PLANNED','IN_PROGRESS','COMPLETED','ROLLED_BACK','ROLLBACK_BLOCKED')");
            });
            e.HasKey(r => r.PlanId).HasName("pk_cutover_plan");
            e.Property(r => r.PlanId).HasColumnName("plan_id");
            e.Property(r => r.LegalEntityId).HasColumnName("legal_entity_id");
            e.Property(r => r.Name).HasColumnName("name");
            e.Property(r => r.Kind).HasColumnName("kind");
            e.Property(r => r.Scope).HasColumnName("scope");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.CreatedBy).HasColumnName("created_by");
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(r => r.RollbackPreviewedAt).HasColumnName("rollback_previewed_at").HasColumnType("timestamptz");
            e.Property(r => r.RolledBackAt).HasColumnName("rolled_back_at").HasColumnType("timestamptz");
            e.Property(r => r.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            e.HasIndex(r => new { r.LegalEntityId, r.Name }).IsUnique().HasDatabaseName("ux_cutover_plan_name");
        });

        model.Entity<CutoverStepRow>(e =>
        {
            e.ToTable("cutover_step", t => t.HasCheckConstraint("ck_cutover_step_status", "status IN ('PENDING','IN_PROGRESS','DONE','ROLLED_BACK')"));
            e.HasKey(r => r.StepId).HasName("pk_cutover_step");
            e.Property(r => r.StepId).HasColumnName("step_id");
            e.Property(r => r.PlanId).HasColumnName("plan_id");
            e.Property(r => r.Seq).HasColumnName("seq");
            e.Property(r => r.Kind).HasColumnName("kind");
            e.Property(r => r.BatchId).HasColumnName("batch_id");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            e.Property(r => r.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamptz");
            e.Property(r => r.DurationMs).HasColumnName("duration_ms");
            e.Property(r => r.Result).HasColumnName("result");
            e.Property(r => r.Note).HasColumnName("note");
            e.HasIndex(r => new { r.PlanId, r.Seq }).IsUnique().HasDatabaseName("ux_cutover_step_seq");
            e.HasOne<CutoverPlanRow>().WithMany().HasForeignKey(r => r.PlanId).HasConstraintName("fk_cutover_step_plan");
        });
    }
}
