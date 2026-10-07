using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Platform.Persistence.Migrations
{
    /// <summary>
    /// F-1b review m5 (hand-written): the application role no longer writes <c>plt.audit_chain_head</c>. The head is
    /// created and locked by the SECURITY DEFINER function <c>plt.audit_chain_lock(date)</c>, and advanced by a BEFORE
    /// INSERT trigger on <c>plt.audit_event</c> that refuses any row that does not extend the day's chain (sequence and
    /// prev_hash). Both run as the owner (the migrator role); the app role gets EXECUTE on the lock function and SELECT on
    /// the head only (PlatformModule.PlatformGrants).
    /// </summary>
    public partial class AuditChainHeadGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION plt.audit_chain_lock(p_chain_date date)
                  RETURNS TABLE (last_sequence bigint, last_hash text)
                  LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, plt AS $$
                BEGIN
                  INSERT INTO plt.audit_chain_head AS h (chain_date, last_sequence, last_hash)
                  VALUES (p_chain_date, 0,
                          encode(sha256(convert_to('coreins:plt.audit_event:genesis:' || to_char(p_chain_date, 'YYYY-MM-DD'), 'UTF8')), 'hex'))
                  ON CONFLICT (chain_date) DO NOTHING;
                  RETURN QUERY
                    SELECT h.last_sequence, h.last_hash::text FROM plt.audit_chain_head h WHERE h.chain_date = p_chain_date FOR UPDATE;
                END
                $$;
                REVOKE ALL ON FUNCTION plt.audit_chain_lock(date) FROM PUBLIC;

                CREATE FUNCTION plt.audit_event_extend_chain() RETURNS trigger
                  LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, plt AS $$
                BEGIN
                  UPDATE plt.audit_chain_head h
                     SET last_sequence = NEW.sequence, last_hash = NEW.hash
                   WHERE h.chain_date = NEW.chain_date AND h.last_sequence = NEW.sequence - 1 AND h.last_hash = NEW.prev_hash;
                  IF NOT FOUND THEN
                    RAISE EXCEPTION 'audit record % of % does not extend the chain', NEW.sequence, NEW.chain_date
                      USING ERRCODE = 'check_violation';
                  END IF;
                  RETURN NEW;
                END
                $$;
                REVOKE ALL ON FUNCTION plt.audit_event_extend_chain() FROM PUBLIC;
                CREATE TRIGGER audit_event_extend_chain BEFORE INSERT ON plt.audit_event
                  FOR EACH ROW EXECUTE FUNCTION plt.audit_event_extend_chain();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS audit_event_extend_chain ON plt.audit_event;
                DROP FUNCTION IF EXISTS plt.audit_event_extend_chain();
                DROP FUNCTION IF EXISTS plt.audit_chain_lock(date);
                """);
        }
    }
}
