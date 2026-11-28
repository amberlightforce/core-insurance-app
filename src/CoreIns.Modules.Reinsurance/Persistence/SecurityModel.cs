using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// <c>ri.security_rule</c>: one security rule set of a legal entity, effective from a date (BO-RI-05, REQ-RI-050, -053). Append-only: a
/// later set supersedes an earlier one from its own date, and older sets stay as history. The ratings are a JSON list of
/// <c>{reinsurerPartyId, rating, maxSharePct}</c> (PTY holds no rating, so the security list lives here).
/// </summary>
internal sealed class SecurityRuleRow
{
    public Guid RuleId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public int VersionNo { get; set; }

    public BusinessDate EffectiveFrom { get; set; }

    public string? RatingFloor { get; set; }

    public decimal? MaxSharePct { get; set; }

    /// <summary>JSON array of the reinsurer ratings (jsonb).</summary>
    public string Ratings { get; set; } = "[]";

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// <c>ri.line_table</c>: one version of the line table of a legal entity (BO-RI-05, REQ-RI-036): bands of sum insured with the
/// retention and the maximum number of lines. Append-only; the rows are a JSON array (jsonb).
/// </summary>
internal sealed class LineTableRow
{
    public Guid TableId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public int VersionNo { get; set; }

    public string Rows { get; set; } = "[]";

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>The EF model of the security rule and line table.</summary>
internal static class SecurityModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SecurityRuleRow>(entity =>
        {
            entity.ToTable("security_rule", table => table.HasCheckConstraint("ck_security_rule_share", "max_share_pct IS NULL OR (max_share_pct > 0 AND max_share_pct <= 100)"));
            entity.HasKey(e => e.RuleId).HasName("pk_security_rule");
            entity.Property(e => e.RuleId).HasColumnName("rule_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from").HasColumnType("date");
            entity.Property(e => e.RatingFloor).HasColumnName("rating_floor");
            entity.Property(e => e.MaxSharePct).HasColumnName("max_share_pct").HasColumnType("numeric(9,6)");
            entity.Property(e => e.Ratings).HasColumnName("ratings").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.LegalEntityId, e.VersionNo }).IsUnique().HasDatabaseName("ux_security_rule_version");
        });

        modelBuilder.Entity<LineTableRow>(entity =>
        {
            entity.ToTable("line_table");
            entity.HasKey(e => e.TableId).HasName("pk_line_table");
            entity.Property(e => e.TableId).HasColumnName("table_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.Rows).HasColumnName("rows").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.HasIndex(e => new { e.LegalEntityId, e.VersionNo }).IsUnique().HasDatabaseName("ux_line_table_version");
        });
    }
}

/// <summary>The SQL the security migration adds beside the EF model: both tables are append-only (PITFALLS 8, 17).</summary>
internal static class SecurityDatabaseSql
{
    public const string Up =
        "CREATE TRIGGER tr_security_rule_seal BEFORE UPDATE OR DELETE ON ri.security_rule FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();\n" +
        "CREATE TRIGGER tr_security_rule_no_truncate BEFORE TRUNCATE ON ri.security_rule FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();\n" +
        "CREATE TRIGGER tr_line_table_seal BEFORE UPDATE OR DELETE ON ri.line_table FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();\n" +
        "CREATE TRIGGER tr_line_table_no_truncate BEFORE TRUNCATE ON ri.line_table FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();\n";

    public const string Down =
        "DROP TRIGGER IF EXISTS tr_security_rule_seal ON ri.security_rule;\nDROP TRIGGER IF EXISTS tr_security_rule_no_truncate ON ri.security_rule;\n" +
        "DROP TRIGGER IF EXISTS tr_line_table_seal ON ri.line_table;\nDROP TRIGGER IF EXISTS tr_line_table_no_truncate ON ri.line_table;\n";
}
