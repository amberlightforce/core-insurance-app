namespace CoreIns.Modules.Market.Persistence;

/// <summary>
/// Database guards of the configuration states (SL5-MKT-STATE, D-SL5-06/07, REQ-MKT-048):
/// <list type="bullet">
/// <item><c>pack_version</c> and <c>config_state</c> are append-only for every role, the owner included: UPDATE, DELETE and
/// TRUNCATE are refused and logged as a SECURITY line (the app role has no such privilege either);</item>
/// <item>a state can be inserted only by a transaction that holds the state advisory lock (<see cref="LockClass"/>, <see cref="LockObject"/>;
/// checked in <c>pg_locks</c> for this backend, not trusted from the caller), only on top of the newest state
/// (<c>parent_hash</c> = the hash with the highest <c>seq</c>, the first state has none), and not before the activation instant
/// of that newest state. So two writers cannot fork the chain and history never runs backwards (PITFALLS 17, 40);</item>
/// <item><c>pack_activation</c> (written by SL5-MKT-ROLLBACK): never deleted; identity and request frozen; the status only moves
/// PENDING_APPROVAL → APPROVED / REJECTED / WITHDRAWN / ACTIVE, APPROVED → ACTIVE, ACTIVE → SUPERSEDED; a decision is made by someone other than the
/// requester and an approval or activation names its approval request; the one row born ACTIVE is the genesis activation of the genesis state
/// (PITFALLS 47).</item>
/// </list>
/// </summary>
internal static class MarketStateSql
{
    /// <summary>First key of <c>pg_advisory_xact_lock(int, int)</c> serialising writers of <c>mkt.config_state</c> (ASCII "MKT" shifted).</summary>
    public const int LockClass = 1296192512;

    /// <summary>Second key of the state lock.</summary>
    public const int LockObject = 1;

    /// <summary>The statement every writer of a state runs first, inside its transaction.</summary>
    public const string TakeLock = "SELECT pg_advisory_xact_lock(1296192512, 1)";

    public const string Up = """
        CREATE FUNCTION mkt.reject_state_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            RAISE LOG 'SECURITY: % on append-only table mkt.% refused for role % (D-SL5-06)', TG_OP, TG_TABLE_NAME, current_user;
            RAISE EXCEPTION 'mkt.% is append-only: % is refused (D-SL5-06)', TG_TABLE_NAME, TG_OP USING ERRCODE = 'restrict_violation';
        END
        $fn$;

        CREATE TRIGGER tr_pack_version_append_only BEFORE UPDATE OR DELETE ON mkt.pack_version
            FOR EACH ROW EXECUTE FUNCTION mkt.reject_state_change();
        CREATE TRIGGER tr_pack_version_no_truncate BEFORE TRUNCATE ON mkt.pack_version
            FOR EACH STATEMENT EXECUTE FUNCTION mkt.reject_state_change();
        CREATE TRIGGER tr_config_state_append_only BEFORE UPDATE OR DELETE ON mkt.config_state
            FOR EACH ROW EXECUTE FUNCTION mkt.reject_state_change();
        CREATE TRIGGER tr_config_state_no_truncate BEFORE TRUNCATE ON mkt.config_state
            FOR EACH STATEMENT EXECUTE FUNCTION mkt.reject_state_change();

        CREATE FUNCTION mkt.guard_config_state_insert() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            newest_hash text;
            newest_at timestamptz;
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_locks
                 WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted
                   AND objsubid = 2 AND classid = 1296192512::oid AND objid = 1::oid) THEN
                RAISE LOG 'SECURITY: mkt.config_state insert without the state lock by role %', current_user;
                RAISE EXCEPTION 'mkt.config_state is written only under the state advisory lock of the same transaction (D-SL5-06)'
                    USING ERRCODE = 'restrict_violation';
            END IF;

            SELECT hash, activated_at INTO newest_hash, newest_at FROM mkt.config_state ORDER BY seq DESC LIMIT 1;
            IF newest_hash IS DISTINCT FROM NEW.parent_hash THEN
                RAISE EXCEPTION 'mkt.config_state is a linear chain: the new state must have the newest state (%) as its parent, not %', newest_hash, NEW.parent_hash
                    USING ERRCODE = 'restrict_violation';
            END IF;
            IF newest_at IS NOT NULL AND NEW.activated_at < newest_at THEN
                RAISE EXCEPTION 'mkt.config_state activation instants never run backwards (% < %)', NEW.activated_at, newest_at
                    USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_config_state_chain BEFORE INSERT ON mkt.config_state
            FOR EACH ROW EXECUTE FUNCTION mkt.guard_config_state_insert();

        CREATE FUNCTION mkt.guard_pack_activation() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE LOG 'SECURITY: DELETE on mkt.pack_activation refused for role %', current_user;
                RAISE EXCEPTION 'mkt.pack_activation rows are never deleted' USING ERRCODE = 'restrict_violation';
            END IF;

            IF TG_OP = 'INSERT' THEN
                -- A request starts pending, undecided and not yet in force.
                IF NEW.status = 'PENDING_APPROVAL' AND NEW.decided_by IS NULL AND NEW.resulting_hash IS NULL AND NEW.activated_at IS NULL THEN
                    RETURN NEW;
                END IF;
                -- The one row born active: the genesis activation the release writes for the state it created, never an approval.
                IF NEW.status = 'ACTIVE' AND NEW.kind = 'ACTIVATE' AND NEW.requested_by = 'system:genesis' AND NEW.decided_by = 'system:genesis'
                   AND NEW.approval_request_id IS NULL AND NEW.activated_at IS NOT NULL AND NEW.supersedes_id IS NULL
                   AND EXISTS (SELECT 1 FROM mkt.config_state WHERE hash = NEW.resulting_hash AND cause = 'GENESIS') THEN
                    RETURN NEW;
                END IF;
                RAISE LOG 'SECURITY: mkt.pack_activation inserted as % (kind %) by role % refused', NEW.status, NEW.kind, current_user;
                RAISE EXCEPTION 'mkt.pack_activation starts PENDING_APPROVAL; only the genesis activation is born ACTIVE' USING ERRCODE = 'restrict_violation';
            END IF;

            IF (NEW.id, NEW.legal_entity_id, NEW.pack_id, NEW.version, NEW.kind, NEW.requested_by, NEW.created_at)
               IS DISTINCT FROM (OLD.id, OLD.legal_entity_id, OLD.pack_id, OLD.version, OLD.kind, OLD.requested_by, OLD.created_at) THEN
                RAISE EXCEPTION 'mkt.pack_activation identity and request are frozen' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (OLD.decided_by IS NOT NULL AND NEW.decided_by IS DISTINCT FROM OLD.decided_by)
               OR (OLD.approval_request_id IS NOT NULL AND NEW.approval_request_id IS DISTINCT FROM OLD.approval_request_id)
               OR (OLD.resulting_hash IS NOT NULL AND NEW.resulting_hash IS DISTINCT FROM OLD.resulting_hash)
               OR (OLD.activated_at IS NOT NULL AND NEW.activated_at IS DISTINCT FROM OLD.activated_at)
               OR (OLD.supersedes_id IS NOT NULL AND NEW.supersedes_id IS DISTINCT FROM OLD.supersedes_id) THEN
                RAISE EXCEPTION 'mkt.pack_activation decision, approval and result are frozen once written' USING ERRCODE = 'restrict_violation';
            END IF;
            IF NEW.status <> OLD.status AND NOT (
                   (OLD.status = 'PENDING_APPROVAL' AND NEW.status IN ('APPROVED', 'REJECTED', 'WITHDRAWN', 'ACTIVE'))
                OR (OLD.status = 'APPROVED' AND NEW.status = 'ACTIVE')
                OR (OLD.status = 'ACTIVE' AND NEW.status = 'SUPERSEDED')) THEN
                RAISE LOG 'SECURITY: mkt.pack_activation % status % -> % refused for role %', OLD.id, OLD.status, NEW.status, current_user;
                RAISE EXCEPTION 'mkt.pack_activation status % cannot move to %', OLD.status, NEW.status USING ERRCODE = 'restrict_violation';
            END IF;
            IF NEW.status = OLD.status AND OLD.status IN ('REJECTED', 'WITHDRAWN', 'SUPERSEDED') AND NEW IS DISTINCT FROM OLD THEN
                RAISE EXCEPTION 'mkt.pack_activation is final in status %', OLD.status USING ERRCODE = 'restrict_violation';
            END IF;
            -- Maker-checker and "approved only with an approval" (PITFALLS 47): whoever decides is not the requester, and approving or activating names the approval.
            IF NEW.status IN ('APPROVED', 'REJECTED', 'ACTIVE') AND OLD.status = 'PENDING_APPROVAL' THEN
                IF NEW.decided_by IS NULL OR NEW.decided_by = NEW.requested_by THEN
                    RAISE LOG 'SECURITY: mkt.pack_activation % decided by the requester or by nobody (role %)', OLD.id, current_user;
                    RAISE EXCEPTION 'mkt.pack_activation must be decided by someone other than the requester' USING ERRCODE = 'restrict_violation';
                END IF;
                IF NEW.status IN ('APPROVED', 'ACTIVE') AND NEW.approval_request_id IS NULL THEN
                    RAISE EXCEPTION 'mkt.pack_activation cannot be approved without an approval request' USING ERRCODE = 'restrict_violation';
                END IF;
            END IF;
            IF NEW.status = 'ACTIVE' AND OLD.status <> 'ACTIVE' AND (NEW.resulting_hash IS NULL OR NEW.activated_at IS NULL) THEN
                RAISE EXCEPTION 'mkt.pack_activation becomes ACTIVE with the state it produced and the instant it took effect' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_pack_activation_guard BEFORE INSERT OR UPDATE OR DELETE ON mkt.pack_activation
            FOR EACH ROW EXECUTE FUNCTION mkt.guard_pack_activation();
        CREATE TRIGGER tr_pack_activation_no_truncate BEFORE TRUNCATE ON mkt.pack_activation
            FOR EACH STATEMENT EXECUTE FUNCTION mkt.reject_state_change();
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS tr_pack_activation_no_truncate ON mkt.pack_activation;
        DROP TRIGGER IF EXISTS tr_pack_activation_guard ON mkt.pack_activation;
        DROP FUNCTION IF EXISTS mkt.guard_pack_activation();
        DROP TRIGGER IF EXISTS tr_config_state_chain ON mkt.config_state;
        DROP FUNCTION IF EXISTS mkt.guard_config_state_insert();
        DROP TRIGGER IF EXISTS tr_config_state_no_truncate ON mkt.config_state;
        DROP TRIGGER IF EXISTS tr_config_state_append_only ON mkt.config_state;
        DROP TRIGGER IF EXISTS tr_pack_version_no_truncate ON mkt.pack_version;
        DROP TRIGGER IF EXISTS tr_pack_version_append_only ON mkt.pack_version;
        DROP FUNCTION IF EXISTS mkt.reject_state_change();
        """;
}
