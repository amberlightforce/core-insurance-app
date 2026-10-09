using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice3Temporal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_job_type",
                schema: "pol",
                table: "job");

            migrationBuilder.AddColumn<DateTime>(
                name: "cancelled_at",
                schema: "pol",
                table: "policy_term",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "predecessor_term_id",
                schema: "pol",
                table: "policy_term",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_recorded_at",
                schema: "pol",
                table: "policy",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "acceptance_channel",
                schema: "pol",
                table: "job",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "accepted_at",
                schema: "pol",
                table: "job",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "accepted_by",
                schema: "pol",
                table: "job",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "base_transaction_id",
                schema: "pol",
                table: "job",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_kind",
                schema: "pol",
                table: "job",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_source",
                schema: "pol",
                table: "job",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "expiring_term_id",
                schema: "pol",
                table: "job",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason_code",
                schema: "pol",
                table: "job",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refund_method",
                schema: "pol",
                table: "job",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sub_state",
                schema: "pol",
                table: "job",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_term_id",
                schema: "pol",
                table: "job",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_source",
                schema: "pol",
                table: "charge_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transaction_kind",
                schema: "pol",
                table: "charge_line",
                type: "text",
                nullable: false,
                defaultValue: "NEW_BUSINESS");

            migrationBuilder.AddColumn<string>(
                name: "treatment_rule_id",
                schema: "pol",
                table: "charge_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "treatment_rule_version",
                schema: "pol",
                table: "charge_line",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_job_open_renewal",
                schema: "pol",
                table: "job",
                column: "expiring_term_id",
                unique: true,
                filter: "job_type = 'RENEWAL' AND state IN ('DRAFT', 'QUOTED', 'SCHEDULED')");

            migrationBuilder.CreateIndex(
                name: "ux_job_open_servicing",
                schema: "pol",
                table: "job",
                columns: new[] { "job_type", "target_term_id" },
                unique: true,
                filter: "job_type IN ('POLICY_CHANGE', 'CANCELLATION') AND state IN ('DRAFT', 'QUOTED', 'SCHEDULED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_cancellation_kind",
                schema: "pol",
                table: "job",
                sql: "cancellation_kind IN ('STANDARD', 'FLAT')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_sub_state",
                schema: "pol",
                table: "job",
                sql: "sub_state IN ('QUICK_QUOTE', 'CONVERTING', 'OFFERED', 'ACCEPTED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_type",
                schema: "pol",
                table: "job",
                sql: "job_type IN ('SUBMISSION', 'POLICY_CHANGE', 'CANCELLATION', 'RENEWAL')");

            // A change or cancellation names the term it acts on; a renewal names the expiring term (the D-SL3-11 indexes key on them).
            migrationBuilder.AddCheckConstraint(
                name: "ck_job_target_term",
                schema: "pol",
                table: "job",
                sql: "job_type = 'SUBMISSION' OR (job_type IN ('POLICY_CHANGE', 'CANCELLATION') AND target_term_id IS NOT NULL) OR (job_type = 'RENEWAL' AND expiring_term_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_charge_line_transaction_kind",
                schema: "pol",
                table: "charge_line",
                sql: "transaction_kind IN ('NEW_BUSINESS', 'ENDORSEMENT_DEBIT', 'ENDORSEMENT_CREDIT', 'CANCELLATION', 'DISTANCE_WITHDRAWAL_VOID', 'VOID', 'RETURN_PREMIUM', 'REINSTATEMENT', 'FEE', 'REFUND')");

            // Existing charge lines are all first issuances: NEW_BUSINESS. The default only fills them; new rows must say
            // what they are (PITFALLS 12: a field a consumer depends on is always set).
            migrationBuilder.Sql("ALTER TABLE pol.charge_line ALTER COLUMN transaction_kind DROP DEFAULT;");

            // D-SL3-03 (a): the per-policy record-time watermark. Backfilled from the latest record of each policy, but never below
            // the migration's own transaction time: a snapshot reference issued before this migration carries knownAt = the clock
            // at issue (>= the last record, <= now) and must stay readable instead of being refused as forged.
            migrationBuilder.Sql("""
                UPDATE pol.policy p SET last_recorded_at = GREATEST(
                    transaction_timestamp(),
                    p.recorded_at,
                    COALESCE((SELECT max(t.recorded_at) FROM pol.policy_transaction t WHERE t.policy_id = p.policy_id), p.recorded_at),
                    COALESCE((SELECT max(c.recorded_at) FROM pol.charge_line c WHERE c.policy_id = p.policy_id), p.recorded_at),
                    COALESCE((SELECT max(GREATEST(v.recorded_from, COALESCE(v.recorded_to, v.recorded_from))) FROM pol.policy_term v WHERE v.policy_id = p.policy_id), p.recorded_at),
                    COALESCE((SELECT max(GREATEST(g.recorded_from, COALESCE(g.recorded_to, g.recorded_from))) FROM pol.segment g WHERE g.policy_id = p.policy_id), p.recorded_at));
                ALTER TABLE pol.policy ALTER COLUMN last_recorded_at SET NOT NULL;
                """);

            // The policy row is frozen (D-SL3-03 b): only last_recorded_at and record_version change, the watermark only
            // forward; never deleted. A new row starts with watermark = recorded_at (the instant it became known).
            migrationBuilder.Sql("""
                -- The watermark may not run away from real time: a writer (or a bug) that pushed it to 2100 would make every later
                -- record time at least that and poison the policy for good. Bounded skew: 1 day, plus the Development dev clock offset
                -- (plt.dev_clock, D-SL3-12, 0 when the table is absent or never advanced), because the application clock then runs
                -- that far ahead of the database clock. SECURITY DEFINER so the app role needs no access to the plt schema.
                CREATE FUNCTION pol.assert_watermark_cap(candidate timestamptz) RETURNS void
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE
                    dev_offset bigint := 0;
                    skew interval;
                BEGIN
                    IF to_regclass('plt.dev_clock') IS NOT NULL THEN
                        EXECUTE 'SELECT COALESCE(max(offset_micros), 0) FROM plt.dev_clock' INTO dev_offset;
                    END IF;
                    skew := interval '1 day' + dev_offset * interval '1 microsecond';
                    IF candidate > clock_timestamp() + skew THEN
                        RAISE EXCEPTION 'pol.policy: the watermark % is more than % ahead of the database clock', candidate, skew
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                END $$;

                CREATE FUNCTION pol.policy_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'pol.policy rows are never deleted' USING ERRCODE = 'restrict_violation';
                    ELSIF TG_OP = 'INSERT' THEN
                        NEW.last_recorded_at := COALESCE(NEW.last_recorded_at, NEW.recorded_at);
                        IF NEW.last_recorded_at <> NEW.recorded_at THEN
                            RAISE EXCEPTION 'pol.policy: a new policy starts with its watermark at recorded_at' USING ERRCODE = 'restrict_violation';
                        END IF;
                        PERFORM pol.assert_watermark_cap(NEW.last_recorded_at);
                        PERFORM set_config('pol.lk_' || replace(NEW.policy_id::text, '-', ''), NEW.last_recorded_at::text, true);
                        RETURN NEW;
                    END IF;
                    IF (to_jsonb(NEW) - 'last_recorded_at' - 'record_version') IS DISTINCT FROM (to_jsonb(OLD) - 'last_recorded_at' - 'record_version') THEN
                        RAISE EXCEPTION 'pol.policy is frozen: only last_recorded_at and record_version may change' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.last_recorded_at <= OLD.last_recorded_at THEN
                        RAISE EXCEPTION 'pol.policy: the record-time watermark moves forward only (% -> %)', OLD.last_recorded_at, NEW.last_recorded_at
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    PERFORM pol.assert_watermark_cap(NEW.last_recorded_at);
                    PERFORM set_config('pol.lk_' || replace(NEW.policy_id::text, '-', ''), NEW.last_recorded_at::text, true);
                    RETURN NEW;
                END $$;
                CREATE TRIGGER tr_policy_frozen BEFORE INSERT OR UPDATE OR DELETE ON pol.policy
                    FOR EACH ROW EXECUTE FUNCTION pol.policy_frozen();

                -- Every record row is stamped with the policy's watermark, i.e. written by a command that took the policy lock and
                -- its record time first (D-SL3-03 a), and advanced the watermark in this very transaction: the policy trigger leaves a
                -- transaction-local mark (pol.lk_<policy>) that this function requires, so a plain SELECT FOR UPDATE, or a committed
                -- watermark reused by an unlocked writer, is refused. (The row's xmin is not usable: EF Core's SaveChanges runs in a
                -- savepoint, so the writer's xid is a subtransaction's.) A writer that skips the lock-then-stamp protocol is refused here, so a
                -- reader's effective knownAt (min(requested, watermark)) can never be overtaken by a later commit.
                CREATE FUNCTION pol.require_stamp() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    watermark timestamptz;
                    stamp timestamptz;
                BEGIN
                    SELECT p.last_recorded_at INTO watermark FROM pol.policy p WHERE p.policy_id = NEW.policy_id;
                    stamp := (to_jsonb(NEW) ->> CASE WHEN TG_TABLE_NAME IN ('policy_transaction', 'charge_line') THEN 'recorded_at' ELSE 'recorded_from' END)::timestamptz;
                    IF watermark IS NULL OR stamp IS DISTINCT FROM watermark THEN
                        RAISE EXCEPTION 'pol.%: a record must be stamped with the policy watermark (stamp %, watermark %); take PolicyWriteLock first',
                            TG_TABLE_NAME, stamp, watermark USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NULLIF(current_setting('pol.lk_' || replace(NEW.policy_id::text, '-', ''), true), '')::timestamptz IS DISTINCT FROM watermark THEN
                        RAISE EXCEPTION 'pol.%: the policy watermark was not advanced in this transaction; take PolicyWriteLock first', TG_TABLE_NAME
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER tr_policy_term_stamp BEFORE INSERT ON pol.policy_term FOR EACH ROW EXECUTE FUNCTION pol.require_stamp();
                CREATE TRIGGER tr_segment_stamp BEFORE INSERT ON pol.segment FOR EACH ROW EXECUTE FUNCTION pol.require_stamp();
                CREATE TRIGGER tr_policy_transaction_stamp BEFORE INSERT ON pol.policy_transaction FOR EACH ROW EXECUTE FUNCTION pol.require_stamp();
                CREATE TRIGGER tr_charge_line_stamp BEFORE INSERT ON pol.charge_line FOR EACH ROW EXECUTE FUNCTION pol.require_stamp();

                -- Closing a record period: at the watermark of this very command (never in the past, never ahead of it), not at the
                -- database clock, which the application clock may lag behind or run ahead of.
                CREATE OR REPLACE FUNCTION pol.only_close_record_period() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    watermark timestamptz;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'pol.% rows are never deleted', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.recorded_to IS NOT NULL OR NEW.recorded_to IS NULL
                       OR (to_jsonb(NEW) - 'recorded_to') IS DISTINCT FROM (to_jsonb(OLD) - 'recorded_to') THEN
                        RAISE EXCEPTION 'pol.% is immutable except closing its record period', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                    END IF;
                    SELECT p.last_recorded_at INTO watermark FROM pol.policy p WHERE p.policy_id = NEW.policy_id;
                    IF NEW.recorded_to IS DISTINCT FROM watermark OR NEW.recorded_to <= OLD.recorded_from THEN
                        RAISE EXCEPTION 'pol.% record periods close at the policy watermark of the closing command (%), never in the past', TG_TABLE_NAME, watermark
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NULLIF(current_setting('pol.lk_' || replace(NEW.policy_id::text, '-', ''), true), '')::timestamptz IS DISTINCT FROM watermark THEN
                        RAISE EXCEPTION 'pol.%: the policy watermark was not advanced in this transaction; take PolicyWriteLock first', TG_TABLE_NAME
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_policy_frozen ON pol.policy;
                DROP TRIGGER IF EXISTS tr_policy_term_stamp ON pol.policy_term;
                DROP TRIGGER IF EXISTS tr_segment_stamp ON pol.segment;
                DROP TRIGGER IF EXISTS tr_policy_transaction_stamp ON pol.policy_transaction;
                DROP TRIGGER IF EXISTS tr_charge_line_stamp ON pol.charge_line;
                DROP FUNCTION IF EXISTS pol.policy_frozen();
                DROP FUNCTION IF EXISTS pol.require_stamp();
                DROP FUNCTION IF EXISTS pol.assert_watermark_cap(timestamptz);
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
                """);

            migrationBuilder.DropIndex(
                name: "ux_job_open_renewal",
                schema: "pol",
                table: "job");

            migrationBuilder.DropIndex(
                name: "ux_job_open_servicing",
                schema: "pol",
                table: "job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_cancellation_kind",
                schema: "pol",
                table: "job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_sub_state",
                schema: "pol",
                table: "job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_target_term",
                schema: "pol",
                table: "job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_type",
                schema: "pol",
                table: "job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_charge_line_transaction_kind",
                schema: "pol",
                table: "charge_line");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                schema: "pol",
                table: "policy_term");

            migrationBuilder.DropColumn(
                name: "predecessor_term_id",
                schema: "pol",
                table: "policy_term");

            migrationBuilder.DropColumn(
                name: "last_recorded_at",
                schema: "pol",
                table: "policy");

            migrationBuilder.DropColumn(
                name: "acceptance_channel",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "accepted_at",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "accepted_by",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "base_transaction_id",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "cancellation_kind",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "cancellation_source",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "expiring_term_id",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "reason_code",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "refund_method",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "sub_state",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "target_term_id",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "cancellation_source",
                schema: "pol",
                table: "charge_line");

            migrationBuilder.DropColumn(
                name: "transaction_kind",
                schema: "pol",
                table: "charge_line");

            migrationBuilder.DropColumn(
                name: "treatment_rule_id",
                schema: "pol",
                table: "charge_line");

            migrationBuilder.DropColumn(
                name: "treatment_rule_version",
                schema: "pol",
                table: "charge_line");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_type",
                schema: "pol",
                table: "job",
                sql: "job_type IN ('SUBMISSION')");
        }
    }
}
