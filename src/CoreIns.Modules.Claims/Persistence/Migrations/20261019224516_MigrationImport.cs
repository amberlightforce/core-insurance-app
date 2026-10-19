using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrationImport : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_import",
                schema: "clm",
                columns: table => new
                {
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legacy_number = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    original_notified_on = table.Column<DateOnly>(type: "date", nullable: false),
                    opening_reserve = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    clock_id = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_import", x => x.claim_id);
                    table.CheckConstraint("ck_migration_import_origin", "origin = 'MIGRATION'");
                    table.CheckConstraint("ck_migration_import_reserve", "opening_reserve >= 0");
                    table.ForeignKey(
                        name: "fk_migration_import_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_import_batch",
                schema: "clm",
                table: "migration_import",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ux_migration_import_legacy",
                schema: "clm",
                table: "migration_import",
                columns: new[] { "legal_entity_id", "legacy_number" },
                unique: true);

            // Reversal of a migrated claim (D-SL6-05): clm.claim rows are never deleted, except inside clm.reverse_migrated_claim, which
            // marks the transaction with clm.migration_reversal = <claim id>. The app role has no DELETE grant; the function is SECURITY
            // DEFINER and refuses unless the claim is a migrated one with no ledger line, payment, recovery, FS case or re-verification.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION clm.claim_identity_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF NULLIF(current_setting('clm.migration_reversal', true), '') = OLD.claim_id::text THEN
                            RETURN OLD;
                        END IF;
                        RAISE EXCEPTION 'clm.claim rows are never deleted' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.claim_id <> OLD.claim_id OR NEW.claim_number <> OLD.claim_number OR NEW.legal_entity_id <> OLD.legal_entity_id
                       OR NEW.jurisdiction <> OLD.jurisdiction OR NEW.policy_id <> OLD.policy_id OR NEW.policy_number <> OLD.policy_number
                       OR NEW.loss_at <> OLD.loss_at OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
                        RAISE EXCEPTION 'clm.claim identity columns are immutable' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE FUNCTION clm.reverse_migrated_claim(p_claim uuid) RETURNS boolean
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, clm AS $$
                BEGIN
                    PERFORM 1 FROM clm.migration_import WHERE claim_id = p_claim;
                    IF NOT FOUND THEN
                        RETURN false;
                    END IF;
                    PERFORM 1 FROM clm.claim WHERE claim_id = p_claim FOR UPDATE;
                    IF EXISTS (SELECT 1 FROM clm.financial_transaction WHERE claim_id = p_claim)
                       OR EXISTS (SELECT 1 FROM clm.transaction_set WHERE claim_id = p_claim)
                       OR EXISTS (SELECT 1 FROM clm.reserve_line WHERE claim_id = p_claim)
                       OR EXISTS (SELECT 1 FROM clm.claim_payment WHERE claim_id = p_claim)
                       OR EXISTS (SELECT 1 FROM clm.recovery WHERE claim_id = p_claim)
                       OR EXISTS (SELECT 1 FROM clm.fs_case WHERE claim_id = p_claim)
                       OR EXISTS (SELECT 1 FROM clm.reverification WHERE claim_id = p_claim)
                       OR EXISTS (SELECT 1 FROM clm.fnol_snapshot WHERE claim_id = p_claim)
                       OR EXISTS (SELECT 1 FROM clm.claim WHERE claim_id = p_claim AND (record_version <> 1 OR reopen_count <> 0)) THEN
                        RETURN false;
                    END IF;
                    PERFORM set_config('clm.migration_reversal', p_claim::text, true);
                    DELETE FROM clm.exposure WHERE claim_id = p_claim;
                    DELETE FROM clm.incident WHERE claim_id = p_claim;
                    DELETE FROM clm.claimant WHERE claim_id = p_claim;
                    DELETE FROM clm.migration_import WHERE claim_id = p_claim;
                    DELETE FROM clm.claim WHERE claim_id = p_claim;
                    PERFORM set_config('clm.migration_reversal', '', true);
                    RETURN true;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS clm.reverse_migrated_claim(uuid);
                CREATE OR REPLACE FUNCTION clm.claim_identity_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'clm.claim rows are never deleted' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.claim_id <> OLD.claim_id OR NEW.claim_number <> OLD.claim_number OR NEW.legal_entity_id <> OLD.legal_entity_id
                       OR NEW.jurisdiction <> OLD.jurisdiction OR NEW.policy_id <> OLD.policy_id OR NEW.policy_number <> OLD.policy_number
                       OR NEW.loss_at <> OLD.loss_at OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
                        RAISE EXCEPTION 'clm.claim identity columns are immutable' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "migration_import",
                schema: "clm");
        }
    }
}
