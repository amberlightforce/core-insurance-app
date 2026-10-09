using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Persistence;

/// <summary>EF model and database guard of the re-verification record (SL3-CLM-REVERIFY).</summary>
internal static class ReverificationModel
{
    public const string CauseUniqueIndex = "ux_reverification_claim_cause";

    public static void Map(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReverificationRow>(entity =>
        {
            entity.ToTable("reverification", table =>
            {
                ClaimsDbContext.CommonChecks(table, "reverification");
                table.HasCheckConstraint("ck_reverification_status", "status IN ('OPEN', 'KEPT', 'ADOPTED')");
                table.HasCheckConstraint("ck_reverification_decided_shape", "(status = 'OPEN') = (decided_at IS NULL)");
                table.HasCheckConstraint("ck_reverification_reason", "status = 'OPEN' OR reason_code IS NOT NULL");
                table.HasCheckConstraint("ck_reverification_refs", "old_snapshot_ref <> new_snapshot_ref");
            });
            entity.HasKey(e => e.ReverificationId).HasName("pk_reverification");
            entity.Property(e => e.ReverificationId).HasColumnName("reverification_id");
            ClaimsDbContext.MapCommon(entity);
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.CauseEventId).HasColumnName("cause_event_id");
            entity.Property(e => e.CauseEventType).HasColumnName("cause_event_type");
            entity.Property(e => e.OldSnapshotRef).HasColumnName("old_snapshot_ref");
            entity.Property(e => e.NewSnapshotRef).HasColumnName("new_snapshot_ref");
            entity.Property(e => e.RaisedAt).HasColumnName("raised_at").HasColumnType("timestamptz");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ReasonCode).HasColumnName("reason_code");
            entity.Property(e => e.CommentEncrypted).HasColumnName("comment_encrypted");
            entity.Property(e => e.CoverageInQuestion).HasColumnName("coverage_in_question");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");

            // REQ-CLM-057: ReverificationRequired once per claim and cause; a redelivery loses on this index.
            entity.HasIndex(e => new { e.ClaimId, e.CauseEventId }).IsUnique().HasDatabaseName(CauseUniqueIndex);
            entity.HasIndex(e => new { e.ClaimId, e.Status }).HasDatabaseName("ix_reverification_claim_status");
            entity.HasOne<ClaimRow>().WithMany().HasForeignKey(e => e.ClaimId).HasConstraintName("fk_reverification_claim").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Trigger SQL of <c>clm.reverification</c>.</summary>
internal static class ReverificationSql
{
    /// <summary>The binding is frozen, the status only moves Open → Kept / Adopted (a decision is final), nothing is deleted.</summary>
    public const string Up = """
        CREATE FUNCTION clm.guard_reverification() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'clm.reverification rows are never deleted' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.reverification_id, NEW.claim_id, NEW.cause_event_id, NEW.cause_event_type, NEW.old_snapshot_ref, NEW.new_snapshot_ref,
                NEW.raised_at, NEW.legal_entity_id, NEW.jurisdiction, NEW.created_at, NEW.created_by)
               IS DISTINCT FROM
               (OLD.reverification_id, OLD.claim_id, OLD.cause_event_id, OLD.cause_event_type, OLD.old_snapshot_ref, OLD.new_snapshot_ref,
                OLD.raised_at, OLD.legal_entity_id, OLD.jurisdiction, OLD.created_at, OLD.created_by)
               OR (OLD.status <> 'OPEN' AND (NEW.status, NEW.reason_code, NEW.comment_encrypted, NEW.decided_at, NEW.decided_by, NEW.coverage_in_question)
                   IS DISTINCT FROM (OLD.status, OLD.reason_code, OLD.comment_encrypted, OLD.decided_at, OLD.decided_by, OLD.coverage_in_question)) THEN
                RAISE LOG 'SECURITY: change of frozen clm.reverification % refused for role %', OLD.reverification_id, current_user;
                RAISE EXCEPTION 'clm.reverification binding is frozen and its decision is final' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_reverification_guard BEFORE UPDATE OR DELETE ON clm.reverification
            FOR EACH ROW EXECUTE FUNCTION clm.guard_reverification();
        CREATE TRIGGER tr_reverification_no_truncate BEFORE TRUNCATE ON clm.reverification
            FOR EACH STATEMENT EXECUTE FUNCTION clm.reject_ledger_change();
        """;

    public const string Down = "DROP FUNCTION IF EXISTS clm.guard_reverification() CASCADE;";
}
