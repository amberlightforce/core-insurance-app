using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Reconciliation;

/// <summary>Break kinds of the fiscal reconciliation (BO-CMP-07): a local fiscal document whose books and channel status disagree.</summary>
internal static class FiscalBreakKinds
{
    /// <summary>The channel rejected the document and nobody has resubmitted it.</summary>
    public const string RejectedByChannel = "REJECTED_BY_CHANNEL";

    /// <summary>The document is still queued: the channel has not registered it.</summary>
    public const string PendingAtChannel = "PENDING_AT_CHANNEL";

    /// <summary>Registered locally but carries no MARK: the channel's registration id is missing.</summary>
    public const string MarkMissing = "MARK_MISSING";

    /// <summary>A credit or cancellation points at an original that does not exist, or a cancellation points at none.</summary>
    public const string CorrelationBroken = "CORRELATION_BROKEN";

    public static IReadOnlyList<string> All { get; } = [RejectedByChannel, PendingAtChannel, MarkMissing, CorrelationBroken];
}

/// <summary>States of a break: OPEN until a later run no longer finds it (then RESOLVED); it re-opens if it comes back.</summary>
internal static class FiscalBreakStatuses
{
    public const string Open = "OPEN";
    public const string Resolved = "RESOLVED";
}

/// <summary>One reconciliation break (cmp.fiscal_reconciliation_break), unique per document and kind so a re-run is idempotent.</summary>
internal sealed class FiscalReconciliationBreakRow
{
    public Guid BreakId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public FiscalDocumentId FiscalDocumentId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string Status { get; set; } = FiscalBreakStatuses.Open;

    public Instant DetectedAt { get; set; }

    public Instant LastSeenAt { get; set; }

    public Instant? ResolvedAt { get; set; }

    /// <summary>Business date up to which the last run compared documents.</summary>
    public BusinessDate RunDate { get; set; }
}

internal static class FiscalReconciliationMapping
{
    public static void MapFiscalReconciliation(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FiscalReconciliationBreakRow>(entity =>
        {
            entity.ToTable("fiscal_reconciliation_break", table =>
            {
                table.HasCheckConstraint("ck_fiscal_reconciliation_break_status", "status IN ('OPEN','RESOLVED')");
                table.HasCheckConstraint("ck_fiscal_reconciliation_break_kind",
                    "kind IN ('REJECTED_BY_CHANNEL','PENDING_AT_CHANNEL','MARK_MISSING','CORRELATION_BROKEN')");
            });
            entity.HasKey(e => e.BreakId).HasName("pk_fiscal_reconciliation_break");
            entity.Property(e => e.BreakId).HasColumnName("break_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.FiscalDocumentId).HasColumnName("fiscal_document_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.Detail).HasColumnName("detail");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.DetectedAt).HasColumnName("detected_at").HasColumnType("timestamptz");
            entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamptz");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at").HasColumnType("timestamptz");
            entity.Property(e => e.RunDate).HasColumnName("run_date");
            entity.HasIndex(e => new { e.LegalEntityId, e.FiscalDocumentId, e.Kind }).IsUnique().HasDatabaseName("ux_fiscal_reconciliation_break_doc_kind");
            entity.HasIndex(e => new { e.LegalEntityId, e.Status }).HasDatabaseName("ix_fiscal_reconciliation_break_status");
        });
    }
}
