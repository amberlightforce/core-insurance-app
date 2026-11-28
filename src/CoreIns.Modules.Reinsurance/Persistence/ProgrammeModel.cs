using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// <c>ri.programme</c>: the identity of a reinsurance programme (BO-RI-01), a group of treaties and their layer ladder for one line and
/// one programme year. The content (name, period, layers) lives in <see cref="ProgrammeVersionRow"/>; the highest version number is current.
/// </summary>
internal sealed class ProgrammeRow
{
    public RiProgrammeId ProgrammeId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Jurisdiction { get; set; } = string.Empty;

    /// <summary>Programme code, unique per legal entity and programme year.</summary>
    public string Code { get; set; } = string.Empty;

    public string LineCode { get; set; } = string.Empty;

    public int ProgrammeYear { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>The programme this one renewed (renew copies the programme to the next year).</summary>
    public RiProgrammeId? RenewedFromId { get; set; }

    public int CurrentVersionNo { get; set; } = 1;

    /// <summary>Everyone who took part in the content, as actor keys; none of them may approve it (BO-RI-02).</summary>
    public string[] Participants { get; set; } = [];

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public Instant UpdatedAt { get; set; }

    public int RecordVersion { get; set; } = 1;
}

/// <summary>
/// <c>ri.programme_version</c>: one version of a programme with its period and status. A DRAFT version is edited in place; every other
/// status seals the content (trigger), so a change to an approved programme is a new version.
/// </summary>
internal sealed class ProgrammeVersionRow
{
    public Guid VersionId { get; set; }

    public RiProgrammeId ProgrammeId { get; set; }

    public int VersionNo { get; set; } = 1;

    /// <summary>DRAFT, PENDING_APPROVAL, ACTIVE, SUPERSEDED or EXPIRED (BO-RI-02 drives the transitions beyond DRAFT).</summary>
    public string Status { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public BusinessDate ValidFrom { get; set; }

    public BusinessDate ValidTo { get; set; }

    public Instant CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string? Reason { get; set; }

    /// <summary>SHA-256 of the content at submit; the approval is bound to it (BO-RI-02).</summary>
    public string? ContentHash { get; set; }

    /// <summary>The PLT approval request of the last submit.</summary>
    public Guid? ApprovalRequestId { get; set; }

    public string? SubmittedBy { get; set; }

    public Instant? SubmittedAt { get; set; }

    public string? DecidedBy { get; set; }

    public Instant? DecidedAt { get; set; }

    /// <summary>The checker's reason when the version was returned to DRAFT.</summary>
    public string? ReturnReason { get; set; }
}

/// <summary><c>ri.programme_layer</c>: a layer of the programme ladder and the contract that carries it (EUR).</summary>
internal sealed class ProgrammeLayerRow
{
    public Guid LayerId { get; set; }

    public Guid VersionId { get; set; }

    public int LayerNo { get; set; }

    public decimal Attachment { get; set; }

    public decimal LimitAmount { get; set; }

    public RiContractId ContractId { get; set; }
}

/// <summary>The EF model of the programme tables.</summary>
internal static class ProgrammeModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProgrammeRow>(entity =>
        {
            entity.ToTable("programme", table =>
            {
                table.HasCheckConstraint("ck_programme_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("ck_programme_currency", "currency = 'EUR'");
                table.HasCheckConstraint("ck_programme_year", "programme_year BETWEEN 1990 AND 2200");
                table.HasCheckConstraint("ck_programme_record_version", "record_version >= 1 AND current_version_no >= 1");
                table.HasCheckConstraint("ck_programme_participants", "cardinality(participants) >= 1");
            });
            entity.HasKey(e => e.ProgrammeId).HasName("pk_programme");
            entity.Property(e => e.ProgrammeId).HasColumnName("programme_id");
            entity.Property(e => e.LegalEntityId).HasColumnName("legal_entity_id");
            entity.Property(e => e.Jurisdiction).HasColumnName("jurisdiction").HasColumnType("char(2)");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.LineCode).HasColumnName("line_code");
            entity.Property(e => e.ProgrammeYear).HasColumnName("programme_year");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.RenewedFromId).HasColumnName("renewed_from_id");
            entity.Property(e => e.CurrentVersionNo).HasColumnName("current_version_no");
            entity.Property(e => e.Participants).HasColumnName("participants");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.RecordVersion).HasColumnName("record_version").IsConcurrencyToken();
            entity.HasIndex(e => new { e.LegalEntityId, e.Code, e.ProgrammeYear }).IsUnique().HasDatabaseName("ux_programme_code_year");
            entity.HasIndex(e => new { e.LegalEntityId, e.LineCode }).HasDatabaseName("ix_programme_line");
            entity.HasIndex(e => new { e.LegalEntityId, e.ProgrammeYear }).HasDatabaseName("ix_programme_year");
            entity.HasOne<ProgrammeRow>().WithMany().HasForeignKey(e => e.RenewedFromId).HasConstraintName("fk_programme_renewed_from").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProgrammeVersionRow>(entity =>
        {
            entity.ToTable("programme_version", table =>
            {
                table.HasCheckConstraint("ck_programme_version_no", "version_no >= 1");
                table.HasCheckConstraint("ck_programme_version_status", "status IN ('DRAFT', 'PENDING_APPROVAL', 'ACTIVE', 'SUPERSEDED', 'EXPIRED')");
                table.HasCheckConstraint("ck_programme_version_period", "valid_to > valid_from");
            });
            entity.HasKey(e => e.VersionId).HasName("pk_programme_version");
            entity.Property(e => e.VersionId).HasColumnName("version_id");
            entity.Property(e => e.ProgrammeId).HasColumnName("programme_id");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from").HasColumnType("date");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to").HasColumnType("date");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.ReturnReason).HasColumnName("return_reason");
            entity.HasIndex(e => new { e.ProgrammeId, e.VersionNo }).IsUnique().HasDatabaseName("ux_programme_version_no");
            entity.HasOne<ProgrammeRow>().WithMany().HasForeignKey(e => e.ProgrammeId).HasConstraintName("fk_programme_version_programme").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProgrammeLayerRow>(entity =>
        {
            entity.ToTable("programme_layer", table =>
            {
                table.HasCheckConstraint("ck_programme_layer_no", "layer_no >= 1");
                table.HasCheckConstraint("ck_programme_layer_amounts", "attachment >= 0 AND limit_amount > 0");
            });
            entity.HasKey(e => e.LayerId).HasName("pk_programme_layer");
            entity.Property(e => e.LayerId).HasColumnName("layer_id");
            entity.Property(e => e.VersionId).HasColumnName("version_id");
            entity.Property(e => e.LayerNo).HasColumnName("layer_no");
            entity.Property(e => e.Attachment).HasColumnName("attachment").HasColumnType("numeric(19,4)");
            entity.Property(e => e.LimitAmount).HasColumnName("limit_amount").HasColumnType("numeric(19,4)");
            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.HasIndex(e => new { e.VersionId, e.LayerNo }).IsUnique().HasDatabaseName("ux_programme_layer_no");
            entity.HasIndex(e => e.ContractId).HasDatabaseName("ix_programme_layer_contract");
            entity.HasOne<ProgrammeVersionRow>().WithMany().HasForeignKey(e => e.VersionId).HasConstraintName("fk_programme_layer_version").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ContractRow>().WithMany().HasForeignKey(e => e.ContractId).HasConstraintName("fk_programme_layer_contract").OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>
/// The SQL the programme migration adds beside the EF model (PITFALLS 8, 17): a programme and its versions are never deleted and keep their
/// identity; the content of a version (name, period, layers) changes only while the version is DRAFT; the status moves only forward.
/// </summary>
internal static class ProgrammeDatabaseSql
{
    public const string Up = """
        CREATE FUNCTION ri.programme_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'ri.programme rows are never deleted (BO-RI-01)' USING ERRCODE = '42501';
            END IF;
            IF NEW.programme_id <> OLD.programme_id OR NEW.legal_entity_id <> OLD.legal_entity_id OR NEW.jurisdiction <> OLD.jurisdiction
               OR NEW.code <> OLD.code OR NEW.line_code <> OLD.line_code OR NEW.programme_year <> OLD.programme_year
               OR NEW.currency <> OLD.currency OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by
               OR NEW.renewed_from_id IS DISTINCT FROM OLD.renewed_from_id THEN
                RAISE EXCEPTION 'ri.programme identity columns are immutable (BO-RI-01)' USING ERRCODE = '42501';
            END IF;
            IF NEW.current_version_no < OLD.current_version_no THEN
                RAISE EXCEPTION 'ri.programme current version never goes back (BO-RI-01)' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE FUNCTION ri.programme_version_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'ri.programme_version rows are never deleted (BO-RI-01)' USING ERRCODE = '42501';
            END IF;
            IF NEW.version_id <> OLD.version_id OR NEW.programme_id <> OLD.programme_id OR NEW.version_no <> OLD.version_no
               OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
                RAISE EXCEPTION 'ri.programme_version identity columns are immutable (BO-RI-01)' USING ERRCODE = '42501';
            END IF;
            IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
                   (OLD.status = 'DRAFT' AND NEW.status IN ('PENDING_APPROVAL', 'SUPERSEDED'))
                OR (OLD.status = 'PENDING_APPROVAL' AND NEW.status IN ('DRAFT', 'ACTIVE'))
                OR (OLD.status = 'ACTIVE' AND NEW.status IN ('SUPERSEDED', 'EXPIRED'))) THEN
                RAISE EXCEPTION 'ri.programme_version cannot move from % to %', OLD.status, NEW.status USING ERRCODE = '23514';
            END IF;
            IF OLD.status <> 'DRAFT' AND (NEW.name <> OLD.name OR NEW.valid_from <> OLD.valid_from OR NEW.valid_to <> OLD.valid_to) THEN
                RAISE EXCEPTION 'ri.programme_version content is sealed once the version leaves DRAFT; make a new version (BO-RI-01)' USING ERRCODE = '42501';
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE FUNCTION ri.programme_layer_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            v_version uuid;
            v_status text;
        BEGIN
            v_version := CASE WHEN TG_OP = 'DELETE' THEN OLD.version_id ELSE NEW.version_id END;
            SELECT status INTO v_status FROM ri.programme_version WHERE version_id = v_version FOR SHARE;
            IF v_status IS DISTINCT FROM 'DRAFT' THEN
                RAISE EXCEPTION 'ri.programme_layer changes only while the version is DRAFT (BO-RI-01)' USING ERRCODE = '42501';
            END IF;
            RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
        END
        $$;

        CREATE TRIGGER tr_programme_guard BEFORE UPDATE OR DELETE ON ri.programme FOR EACH ROW EXECUTE FUNCTION ri.programme_guard();
        CREATE TRIGGER tr_programme_no_truncate BEFORE TRUNCATE ON ri.programme FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();
        CREATE TRIGGER tr_programme_version_guard BEFORE UPDATE OR DELETE ON ri.programme_version FOR EACH ROW EXECUTE FUNCTION ri.programme_version_guard();
        CREATE TRIGGER tr_programme_version_no_truncate BEFORE TRUNCATE ON ri.programme_version FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();
        CREATE TRIGGER tr_programme_layer_guard BEFORE INSERT OR UPDATE OR DELETE ON ri.programme_layer FOR EACH ROW EXECUTE FUNCTION ri.programme_layer_guard();
        CREATE TRIGGER tr_programme_layer_no_truncate BEFORE TRUNCATE ON ri.programme_layer FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();

        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS tr_programme_layer_no_truncate ON ri.programme_layer;
        DROP TRIGGER IF EXISTS tr_programme_layer_guard ON ri.programme_layer;
        DROP TRIGGER IF EXISTS tr_programme_version_no_truncate ON ri.programme_version;
        DROP TRIGGER IF EXISTS tr_programme_version_guard ON ri.programme_version;
        DROP TRIGGER IF EXISTS tr_programme_no_truncate ON ri.programme;
        DROP TRIGGER IF EXISTS tr_programme_guard ON ri.programme;
        DROP FUNCTION IF EXISTS ri.programme_layer_guard();
        DROP FUNCTION IF EXISTS ri.programme_version_guard();
        DROP FUNCTION IF EXISTS ri.programme_guard();

        """;
}
