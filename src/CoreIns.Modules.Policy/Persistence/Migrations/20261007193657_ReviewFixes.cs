using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legal_status",
                schema: "pol",
                table: "charge_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "provisional",
                schema: "pol",
                table: "charge_line",
                type: "boolean",
                nullable: true);

            // SL-POL review M1: as-known history can only move forward. A term version or segment closes its record period
            // once, never in the past (a close in the past would erase what was known), and only when a successor version is
            // recorded at that instant or a terminating transaction (cancellation, void, rewrite) is recorded then. The
            // successor check is a deferred constraint trigger so close-then-insert inside one transaction works.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION pol.only_close_record_period() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'pol.% rows are never deleted', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.recorded_to IS NOT NULL OR NEW.recorded_to IS NULL
                       OR (to_jsonb(NEW) - 'recorded_to') IS DISTINCT FROM (to_jsonb(OLD) - 'recorded_to') THEN
                        RAISE EXCEPTION 'pol.% is immutable except closing its record period', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.recorded_to < transaction_timestamp() THEN
                        RAISE EXCEPTION 'pol.% record periods close now or later, never in the past', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE FUNCTION pol.require_successor() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    succeeded boolean;
                BEGIN
                    IF TG_TABLE_NAME = 'policy_term' THEN
                        SELECT EXISTS (SELECT 1 FROM pol.policy_term s WHERE s.term_id = NEW.term_id AND s.recorded_from = NEW.recorded_to) INTO succeeded;
                    ELSE
                        SELECT EXISTS (SELECT 1 FROM pol.segment s WHERE s.term_id = NEW.term_id AND s.recorded_from = NEW.recorded_to) INTO succeeded;
                    END IF;
                    IF NOT succeeded AND NOT EXISTS (
                        SELECT 1 FROM pol.policy_transaction t
                         WHERE t.policy_id = NEW.policy_id AND t.recorded_at = NEW.recorded_to AND t.kind IN ('CANCELLATION', 'VOID', 'REWRITE')) THEN
                        RAISE EXCEPTION 'pol.%: a closed record period needs a successor version recorded at % or a recorded termination',
                            TG_TABLE_NAME, NEW.recorded_to USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER tr_policy_term_successor AFTER UPDATE OF recorded_to ON pol.policy_term
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pol.require_successor();
                CREATE CONSTRAINT TRIGGER tr_segment_successor AFTER UPDATE OF recorded_to ON pol.segment
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pol.require_successor();

                -- SL-POL review minor 2: a job never leaves a terminal state (contract §3.2.4), whoever writes the row.
                CREATE FUNCTION pol.job_terminal_state() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.state IN ('BOUND', 'RESCINDED', 'WITHDRAWN', 'DECLINED', 'NOT_TAKEN', 'EXPIRED') AND NEW.state IS DISTINCT FROM OLD.state THEN
                        RAISE EXCEPTION 'pol.job % is %: a terminal state is final', OLD.job_id, OLD.state USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER tr_job_terminal_state BEFORE UPDATE OF state ON pol.job FOR EACH ROW EXECUTE FUNCTION pol.job_terminal_state();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_job_terminal_state ON pol.job;
                DROP TRIGGER IF EXISTS tr_policy_term_successor ON pol.policy_term;
                DROP TRIGGER IF EXISTS tr_segment_successor ON pol.segment;
                DROP FUNCTION IF EXISTS pol.job_terminal_state();
                DROP FUNCTION IF EXISTS pol.require_successor();
                """);

            migrationBuilder.DropColumn(
                name: "legal_status",
                schema: "pol",
                table: "charge_line");

            migrationBuilder.DropColumn(
                name: "provisional",
                schema: "pol",
                table: "charge_line");
        }
    }
}
