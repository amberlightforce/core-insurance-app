using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrationImport : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_policy_transaction_kind",
                schema: "pol",
                table: "policy_transaction");

            migrationBuilder.AddColumn<bool>(
                name: "opening",
                schema: "pol",
                table: "charge_line",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "migration_import",
                schema: "pol",
                columns: table => new
                {
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legacy_number = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    baseline_hash = table.Column<string>(type: "text", nullable: true),
                    imported_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_import", x => x.policy_id);
                    table.CheckConstraint("ck_migration_import_mode", "mode IN ('OPENING', 'RENEWAL_BASED')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_policy_transaction_kind",
                schema: "pol",
                table: "policy_transaction",
                sql: "kind IN ('ISSUANCE', 'CHANGE', 'CANCELLATION', 'REINSTATEMENT', 'REWRITE', 'RENEWAL', 'CARRY_FORWARD', 'SUSPENSION', 'REACTIVATION', 'VOID', 'REVERSAL', 'OPENING')");

            migrationBuilder.CreateIndex(
                name: "ix_migration_import_batch",
                schema: "pol",
                table: "migration_import",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ux_migration_import_legacy",
                schema: "pol",
                table: "migration_import",
                columns: new[] { "legal_entity_id", "legacy_number" },
                unique: true);

            // Reversal of a migrated policy (D-SL6-05): the append-only triggers let a DELETE through only inside
            // pol.reverse_migrated_policy, which marks the transaction with pol.migration_reversal = <policy id>. The app role has no
            // DELETE grant; the function is SECURITY DEFINER (runs as the table owner) and refuses unless the policy is a migrated one
            // that still has nothing but its OPENING transaction, no job and no superseded record.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION pol.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' AND NULLIF(current_setting('pol.migration_reversal', true), '') = OLD.policy_id::text THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'pol.% is append-only', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                END $$;

                CREATE OR REPLACE FUNCTION pol.only_close_record_period() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    watermark timestamptz;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF NULLIF(current_setting('pol.migration_reversal', true), '') = OLD.policy_id::text THEN
                            RETURN OLD;
                        END IF;
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

                CREATE OR REPLACE FUNCTION pol.policy_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF NULLIF(current_setting('pol.migration_reversal', true), '') = OLD.policy_id::text THEN
                            RETURN OLD;
                        END IF;
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

                CREATE FUNCTION pol.reverse_migrated_policy(p_policy uuid) RETURNS boolean
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pol AS $$
                BEGIN
                    PERFORM 1 FROM pol.migration_import WHERE policy_id = p_policy;
                    IF NOT FOUND THEN
                        RETURN false;
                    END IF;
                    PERFORM 1 FROM pol.policy WHERE policy_id = p_policy FOR UPDATE;
                    IF EXISTS (SELECT 1 FROM pol.policy_transaction WHERE policy_id = p_policy AND kind <> 'OPENING')
                       OR EXISTS (SELECT 1 FROM pol.job WHERE policy_id = p_policy)
                       OR EXISTS (SELECT 1 FROM pol.policy_term WHERE policy_id = p_policy AND (term_number <> 1 OR recorded_to IS NOT NULL))
                       OR EXISTS (SELECT 1 FROM pol.segment WHERE policy_id = p_policy AND recorded_to IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM pol.pack_rollback_exception WHERE policy_id = p_policy)
                       OR EXISTS (SELECT 1 FROM pol.oos_correction WHERE policy_id = p_policy) THEN
                        RETURN false;
                    END IF;
                    PERFORM set_config('pol.migration_reversal', p_policy::text, true);
                    DELETE FROM pol.charge_line WHERE policy_id = p_policy;
                    DELETE FROM pol.segment WHERE policy_id = p_policy;
                    DELETE FROM pol.policy_term WHERE policy_id = p_policy;
                    DELETE FROM pol.policy_transaction WHERE policy_id = p_policy;
                    DELETE FROM pol.migration_import WHERE policy_id = p_policy;
                    DELETE FROM pol.policy WHERE policy_id = p_policy;
                    PERFORM set_config('pol.migration_reversal', '', true);
                    RETURN true;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS pol.reverse_migrated_policy(uuid);
                CREATE OR REPLACE FUNCTION pol.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'pol.% is append-only', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "migration_import",
                schema: "pol");

            migrationBuilder.DropCheckConstraint(
                name: "ck_policy_transaction_kind",
                schema: "pol",
                table: "policy_transaction");

            migrationBuilder.DropColumn(
                name: "opening",
                schema: "pol",
                table: "charge_line");

            migrationBuilder.AddCheckConstraint(
                name: "ck_policy_transaction_kind",
                schema: "pol",
                table: "policy_transaction",
                sql: "kind IN ('ISSUANCE', 'CHANGE', 'CANCELLATION', 'REINSTATEMENT', 'REWRITE', 'RENEWAL', 'CARRY_FORWARD', 'SUSPENSION', 'REACTIVATION', 'VOID', 'REVERSAL')");
        }
    }
}
