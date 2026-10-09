namespace CoreIns.Modules.Reinsurance.Persistence;

/// <summary>
/// The SQL the first migration adds beside the EF model (EF cannot express it): the exclusion constraint on the valid
/// period per contract and the integrity triggers. They bind every writer, the app role included (PITFALLS 8, 17, 40).
/// <list type="bullet">
/// <item><b>Contract header</b>: identity columns frozen, never deleted, the status moves only along the lifecycle, and a
/// header reaches APPROVED / ACTIVE / EXPIRED only while its current version is approved.</item>
/// <item><b>Contract version</b>: never deleted; once approved (<c>approved_at</c> set) no column can change (REQ-RI-057).
/// Approving seals exactly the content that was submitted (valid period, placed %, hash and revision unchanged).</item>
/// <item><b>Section, layer, clause, participation</b>: append-only; a row can be added only to an unapproved version and
/// only as the version's current revision (a draft edit adds a new revision, the old one stays as history). The guard
/// reads the version <c>FOR SHARE</c>, so an approval racing an insert either goes first (the insert fails) or waits.</item>
/// </list>
/// </summary>
internal static class ReinsuranceDatabaseSql
{
    private static readonly string[] ChildTables = ["section", "layer", "clause", "participation"];

    /// <summary>Statements run by the migration's Up.</summary>
    public static readonly string Up = """
        ALTER TABLE ri.contract_version
            ADD CONSTRAINT ex_contract_version_period
            EXCLUDE USING gist (contract_id WITH =, daterange(valid_from, valid_to, '[)') WITH &&) WHERE (known_to IS NULL);

        CREATE FUNCTION ri.contract_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'ri.contract rows are never deleted (REQ-RI-065)' USING ERRCODE = '42501';
            END IF;
            IF NEW.contract_id IS DISTINCT FROM OLD.contract_id
               OR NEW.legal_entity_id IS DISTINCT FROM OLD.legal_entity_id
               OR NEW.jurisdiction IS DISTINCT FROM OLD.jurisdiction
               OR NEW.contract_number IS DISTINCT FROM OLD.contract_number
               OR NEW.stable_treaty_id IS DISTINCT FROM OLD.stable_treaty_id
               OR NEW.contract_type IS DISTINCT FROM OLD.contract_type
               OR NEW.contract_year IS DISTINCT FROM OLD.contract_year
               OR NEW.currency IS DISTINCT FROM OLD.currency
               OR NEW.created_at IS DISTINCT FROM OLD.created_at
               OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                RAISE EXCEPTION 'ri.contract identity columns are immutable' USING ERRCODE = '42501';
            END IF;
            IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
                   (OLD.status = 'DRAFT' AND NEW.status = 'PENDING_APPROVAL')
                OR (OLD.status = 'PENDING_APPROVAL' AND NEW.status IN ('DRAFT', 'APPROVED'))
                OR (OLD.status = 'APPROVED' AND NEW.status = 'ACTIVE')
                OR (OLD.status = 'ACTIVE' AND NEW.status = 'EXPIRED')) THEN
                RAISE EXCEPTION 'ri.contract cannot move from % to %', OLD.status, NEW.status USING ERRCODE = '23514';
            END IF;
            IF NEW.status IS DISTINCT FROM OLD.status AND NEW.status IN ('APPROVED', 'ACTIVE', 'EXPIRED') THEN
                PERFORM 1 FROM ri.contract_version v
                 WHERE v.contract_id = NEW.contract_id AND v.known_to IS NULL AND v.approved_at IS NOT NULL
                   FOR SHARE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'ri.contract % needs an approved version to become %', NEW.contract_id, NEW.status USING ERRCODE = '23514';
                END IF;
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE TRIGGER tr_contract_guard BEFORE UPDATE OR DELETE ON ri.contract
            FOR EACH ROW EXECUTE FUNCTION ri.contract_guard();

        CREATE FUNCTION ri.contract_version_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'ri.contract_version rows are never deleted (REQ-RI-065)' USING ERRCODE = '42501';
            END IF;
            IF OLD.approved_at IS NOT NULL THEN
                RAISE EXCEPTION 'ri.contract_version % is approved and immutable (REQ-RI-057)', OLD.version_id USING ERRCODE = '42501';
            END IF;
            IF NEW.version_id IS DISTINCT FROM OLD.version_id
               OR NEW.contract_id IS DISTINCT FROM OLD.contract_id
               OR NEW.version_no IS DISTINCT FROM OLD.version_no
               OR NEW.known_from IS DISTINCT FROM OLD.known_from
               OR NEW.created_at IS DISTINCT FROM OLD.created_at
               OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
                RAISE EXCEPTION 'ri.contract_version identity columns are immutable' USING ERRCODE = '42501';
            END IF;
            IF NEW.approved_at IS NOT NULL THEN
                IF NEW.valid_from IS DISTINCT FROM OLD.valid_from
                   OR NEW.valid_to IS DISTINCT FROM OLD.valid_to
                   OR NEW.placed_pct IS DISTINCT FROM OLD.placed_pct
                   OR NEW.content_hash IS DISTINCT FROM OLD.content_hash
                   OR NEW.content_rev IS DISTINCT FROM OLD.content_rev
                   OR NEW.known_to IS DISTINCT FROM OLD.known_to
                   OR NEW.approval_request_id IS DISTINCT FROM OLD.approval_request_id THEN
                    RAISE EXCEPTION 'approving ri.contract_version % must not change the submitted content', OLD.version_id USING ERRCODE = '42501';
                END IF;
                PERFORM 1 FROM ri.contract c
                 WHERE c.contract_id = NEW.contract_id AND c.status = 'PENDING_APPROVAL'
                   FOR SHARE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'ri.contract_version % can be approved only while its contract is pending approval', OLD.version_id USING ERRCODE = '23514';
                END IF;
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE TRIGGER tr_contract_version_guard BEFORE UPDATE OR DELETE ON ri.contract_version
            FOR EACH ROW EXECUTE FUNCTION ri.contract_version_guard();

        CREATE FUNCTION ri.content_insert_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            version RECORD;
        BEGIN
            SELECT approved_at, content_rev INTO version FROM ri.contract_version WHERE version_id = NEW.version_id FOR SHARE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'ri.% needs an existing contract version', TG_TABLE_NAME USING ERRCODE = '23503';
            END IF;
            IF version.approved_at IS NOT NULL THEN
                RAISE EXCEPTION 'ri.% rows cannot be added to an approved version (REQ-RI-057)', TG_TABLE_NAME USING ERRCODE = '42501';
            END IF;
            IF NEW.rev IS DISTINCT FROM version.content_rev THEN
                RAISE EXCEPTION 'ri.% rows must carry the version''s current revision %', TG_TABLE_NAME, version.content_rev USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE FUNCTION ri.content_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'ri.% rows are append-only (REQ-RI-065, PITFALLS 17)', TG_TABLE_NAME USING ERRCODE = '42501';
        END
        $$;

        CREATE TRIGGER tr_contract_no_truncate BEFORE TRUNCATE ON ri.contract
            FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();
        CREATE TRIGGER tr_contract_version_no_truncate BEFORE TRUNCATE ON ri.contract_version
            FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();
        """ + "\n" + ChildTriggers("CREATE");

    /// <summary>Statements run by the migration's Down (before the tables are dropped).</summary>
    public static readonly string Down = ChildTriggers("DROP") + """

        DROP TRIGGER IF EXISTS tr_contract_version_no_truncate ON ri.contract_version;
        DROP TRIGGER IF EXISTS tr_contract_no_truncate ON ri.contract;
        DROP TRIGGER IF EXISTS tr_contract_version_guard ON ri.contract_version;
        DROP TRIGGER IF EXISTS tr_contract_guard ON ri.contract;
        DROP FUNCTION IF EXISTS ri.content_append_only();
        DROP FUNCTION IF EXISTS ri.content_insert_guard();
        DROP FUNCTION IF EXISTS ri.contract_version_guard();
        DROP FUNCTION IF EXISTS ri.contract_guard();
        """;

    private static string ChildTriggers(string verb)
    {
        var sql = new System.Text.StringBuilder();
        foreach (var table in ChildTables)
        {
            if (verb == "CREATE")
            {
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"CREATE TRIGGER tr_{table}_insert_guard BEFORE INSERT ON ri.{table} FOR EACH ROW EXECUTE FUNCTION ri.content_insert_guard();");
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"CREATE TRIGGER tr_{table}_append_only BEFORE UPDATE OR DELETE ON ri.{table} FOR EACH ROW EXECUTE FUNCTION ri.content_append_only();");
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"CREATE TRIGGER tr_{table}_no_truncate BEFORE TRUNCATE ON ri.{table} FOR EACH STATEMENT EXECUTE FUNCTION ri.content_append_only();");
            }
            else
            {
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS tr_{table}_insert_guard ON ri.{table};");
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS tr_{table}_append_only ON ri.{table};");
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS tr_{table}_no_truncate ON ri.{table};");
            }
        }

        return sql.ToString();
    }
}
