using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// <c>ri.statement</c>: the account statement of one reinsurer for one period and currency (BO-RI-14). The content (totals, line count, hash) is
/// sealed at generation by a trigger; only the workflow columns (status, submit, approve, issue, response, superseded by) change. A correction
/// is a new version (<see cref="Version"/>, <see cref="PreviousStatementId"/>) that supersedes this one.
/// </summary>
internal sealed class StatementRow
{
    public Guid StatementId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public Guid ReinsurerPartyId { get; set; }

    /// <summary>Accounting period, yyyy-MM.</summary>
    public string Period { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public int Version { get; set; }

    public Guid? PreviousStatementId { get; set; }

    /// <summary>DRAFT, SUBMITTED, APPROVED, ISSUED, AGREED, DISPUTED or SUPERSEDED.</summary>
    public string Status { get; set; } = string.Empty;

    public decimal Opening { get; set; }

    public decimal Premium { get; set; }

    public decimal Commission { get; set; }

    public decimal Recoveries { get; set; }

    public decimal Movement { get; set; }

    public decimal Closing { get; set; }

    public int LineCount { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant? SubmittedAt { get; set; }

    public string? SubmittedBy { get; set; }

    public Instant? ApprovedAt { get; set; }

    public string? ApprovedBy { get; set; }

    public Instant? IssuedAt { get; set; }

    public string? IssuedBy { get; set; }

    /// <summary>AGREED or DISPUTED, once the reinsurer answered.</summary>
    public string? Response { get; set; }

    public string? ResponseReason { get; set; }

    public Instant? RespondedAt { get; set; }

    public string? RespondedBy { get; set; }

    public Guid? SupersededBy { get; set; }
}

/// <summary><c>ri.statement_line</c>: one account item copied onto a statement. Append-only.</summary>
internal sealed class StatementLineRow
{
    public Guid StatementId { get; set; }

    public int LineNo { get; set; }

    public Guid ItemId { get; set; }

    public RiContractId ContractId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public BusinessDate ItemDate { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>The EF model of <c>ri.statement</c> and <c>ri.statement_line</c>.</summary>
internal static class StatementModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StatementRow>(entity =>
        {
            entity.ToTable("statement", table =>
            {
                table.HasCheckConstraint("ck_statement_status", "status IN ('DRAFT', 'SUBMITTED', 'APPROVED', 'ISSUED', 'AGREED', 'DISPUTED', 'SUPERSEDED')");
                table.HasCheckConstraint("ck_statement_period", "period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                table.HasCheckConstraint("ck_statement_response", "response IS NULL OR response IN ('AGREED', 'DISPUTED')");
            });
            entity.HasKey(e => e.StatementId).HasName("pk_statement");
            entity.Property(e => e.StatementId).HasColumnName("statement_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.ReinsurerPartyId).HasColumnName("reinsurer_party_id");
            entity.Property(e => e.Period).HasColumnName("period");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Version).HasColumnName("version");
            entity.Property(e => e.PreviousStatementId).HasColumnName("previous_statement_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Opening).HasColumnName("opening").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Premium).HasColumnName("premium").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Commission).HasColumnName("commission").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Recoveries).HasColumnName("recoveries").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Movement).HasColumnName("movement").HasColumnType("numeric(19,4)");
            entity.Property(e => e.Closing).HasColumnName("closing").HasColumnType("numeric(19,4)");
            entity.Property(e => e.LineCount).HasColumnName("line_count");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.IssuedAt).HasColumnName("issued_at").HasColumnType("timestamptz");
            entity.Property(e => e.IssuedBy).HasColumnName("issued_by");
            entity.Property(e => e.Response).HasColumnName("response");
            entity.Property(e => e.ResponseReason).HasColumnName("response_reason");
            entity.Property(e => e.RespondedAt).HasColumnName("responded_at").HasColumnType("timestamptz");
            entity.Property(e => e.RespondedBy).HasColumnName("responded_by");
            entity.Property(e => e.SupersededBy).HasColumnName("superseded_by");

            // One live statement per reinsurer, period and currency; a reissue supersedes the old one first.
            entity.HasIndex(e => new { e.LegalEntityId, e.ReinsurerPartyId, e.Period, e.Currency }).IsUnique().HasFilter("status <> 'SUPERSEDED'")
                .HasDatabaseName("ux_statement_live");
            entity.HasIndex(e => new { e.LegalEntityId, e.ReinsurerPartyId, e.Period, e.Currency, e.Version }).IsUnique().HasDatabaseName("ux_statement_version");
        });

        modelBuilder.Entity<StatementLineRow>(entity =>
        {
            entity.ToTable("statement_line");
            entity.HasKey(e => new { e.StatementId, e.LineNo }).HasName("pk_statement_line");
            entity.Property(e => e.StatementId).HasColumnName("statement_id");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.ItemId).HasColumnName("item_id");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.SourceKind).HasColumnName("source_kind");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.ItemDate).HasColumnName("item_date").HasColumnType("date");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
            entity.HasOne<StatementRow>().WithMany().HasForeignKey(e => e.StatementId).HasConstraintName("fk_statement_line_statement").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>The SQL the statement migration adds beside the EF model: the content seal and the line seal (PITFALLS 8, 17).</summary>
internal static class StatementDatabaseSql
{
    public const string Up = """
        CREATE FUNCTION ri.statement_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'ri.statement rows are never deleted (BO-RI-14)' USING ERRCODE = '42501';
            END IF;
            IF NEW.statement_id <> OLD.statement_id OR NEW.legal_entity_id <> OLD.legal_entity_id OR NEW.reinsurer_party_id <> OLD.reinsurer_party_id
               OR NEW.period <> OLD.period OR NEW.currency <> OLD.currency OR NEW.version <> OLD.version
               OR NEW.previous_statement_id IS DISTINCT FROM OLD.previous_statement_id
               OR NEW.opening <> OLD.opening OR NEW.premium <> OLD.premium OR NEW.commission <> OLD.commission OR NEW.recoveries <> OLD.recoveries
               OR NEW.movement <> OLD.movement OR NEW.closing <> OLD.closing OR NEW.line_count <> OLD.line_count OR NEW.content_hash <> OLD.content_hash
               OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
                RAISE EXCEPTION 'ri.statement content is sealed at generation (BO-RI-14)' USING ERRCODE = '42501';
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE TRIGGER tr_statement_guard BEFORE UPDATE OR DELETE ON ri.statement FOR EACH ROW EXECUTE FUNCTION ri.statement_guard();
        CREATE TRIGGER tr_statement_no_truncate BEFORE TRUNCATE ON ri.statement FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();
        CREATE TRIGGER tr_statement_line_seal BEFORE UPDATE OR DELETE ON ri.statement_line FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();
        CREATE TRIGGER tr_statement_line_no_truncate BEFORE TRUNCATE ON ri.statement_line FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();

        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS tr_statement_line_no_truncate ON ri.statement_line;
        DROP TRIGGER IF EXISTS tr_statement_line_seal ON ri.statement_line;
        DROP TRIGGER IF EXISTS tr_statement_no_truncate ON ri.statement;
        DROP TRIGGER IF EXISTS tr_statement_guard ON ri.statement;
        DROP FUNCTION IF EXISTS ri.statement_guard();

        """;
}
