namespace CoreIns.Modules.Market.Persistence;

/// <summary>
/// Database guards of the configuration states (SL5-MKT-STATE, D-SL5-06/07, REQ-MKT-048):
/// <list type="bullet">
/// <item><c>pack_version</c> and <c>config_state</c> are append-only for every role, the owner included: UPDATE, DELETE and
/// TRUNCATE are refused and logged as a SECURITY line (the app role has no such privilege either);</item>
/// <item>a state can be inserted only by a transaction that holds the state advisory lock (<see cref="LockClass"/>, <see cref="LockObject"/>;
/// checked in <c>pg_locks</c> for this backend, not trusted from the caller), only on top of the newest state
/// (<c>parent_hash</c> = the hash with the highest <c>seq</c>, the first state has none), and not before the activation instant
/// of that newest state. So two writers cannot fork the chain and history never runs backwards (PITFALLS 17, 40).</item>
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
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS tr_config_state_chain ON mkt.config_state;
        DROP FUNCTION IF EXISTS mkt.guard_config_state_insert();
        DROP TRIGGER IF EXISTS tr_config_state_no_truncate ON mkt.config_state;
        DROP TRIGGER IF EXISTS tr_config_state_append_only ON mkt.config_state;
        DROP TRIGGER IF EXISTS tr_pack_version_no_truncate ON mkt.pack_version;
        DROP TRIGGER IF EXISTS tr_pack_version_append_only ON mkt.pack_version;
        DROP FUNCTION IF EXISTS mkt.reject_state_change();
        """;
}
