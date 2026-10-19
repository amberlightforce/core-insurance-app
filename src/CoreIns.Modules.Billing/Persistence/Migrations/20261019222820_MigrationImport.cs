using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrationImport : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_mandate",
                schema: "bil",
                columns: table => new
                {
                    mandate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mandate_reference = table.Column<string>(type: "text", nullable: false),
                    signature_date = table.Column<DateOnly>(type: "date", nullable: false),
                    iban_masked = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "ACTIVE"),
                    origin = table.Column<string>(type: "text", nullable: false, defaultValue: "MIGRATION"),
                    first_used_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    imported_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    imported_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_mandate", x => x.mandate_id);
                    table.CheckConstraint("ck_migration_mandate_origin", "origin = 'MIGRATION'");
                    table.CheckConstraint("ck_migration_mandate_status", "status IN ('ACTIVE', 'CANCELLED')");
                    table.ForeignKey(
                        name: "FK_migration_mandate_billing_account_billing_account_id",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_receivable",
                schema: "bil",
                columns: table => new
                {
                    receivable_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legacy_invoice_number = table.Column<string>(type: "text", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_legacy_number = table.Column<string>(type: "text", nullable: true),
                    original_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    outstanding_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    collected_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false, defaultValue: 0m),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false, defaultValue: "MIGRATION"),
                    imported_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    imported_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_receivable", x => x.receivable_id);
                    table.CheckConstraint("ck_migration_receivable_amounts", "original_amount >= outstanding_amount AND outstanding_amount > 0 AND collected_amount >= 0 AND outstanding_amount = round(outstanding_amount, 2) AND original_amount = round(original_amount, 2)");
                    table.CheckConstraint("ck_migration_receivable_origin", "origin = 'MIGRATION'");
                    table.ForeignKey(
                        name: "FK_migration_receivable_billing_account_billing_account_id",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_mandate_batch",
                schema: "bil",
                table: "migration_mandate",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "IX_migration_mandate_billing_account_id",
                schema: "bil",
                table: "migration_mandate",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ux_migration_mandate_reference",
                schema: "bil",
                table: "migration_mandate",
                columns: new[] { "legal_entity_id", "mandate_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_receivable_batch",
                schema: "bil",
                table: "migration_receivable",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "IX_migration_receivable_billing_account_id",
                schema: "bil",
                table: "migration_receivable",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ux_migration_receivable_legacy",
                schema: "bil",
                table: "migration_receivable",
                columns: new[] { "legal_entity_id", "legacy_invoice_number" },
                unique: true);

            // Reversal of migrated objects (D-SL6-05): the app role has no DELETE grant on the migration tables. This SECURITY DEFINER
            // function (runs as the table owner) deletes one object only while it has no post-migration activity, under the row lock.
            migrationBuilder.Sql("""
                CREATE FUNCTION bil.reverse_migrated(p_kind text, p_id uuid) RETURNS boolean
                    LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, bil AS $$
                BEGIN
                    IF p_kind = 'RECEIVABLE' THEN
                        PERFORM 1 FROM bil.migration_receivable WHERE receivable_id = p_id AND collected_amount = 0 FOR UPDATE;
                        IF NOT FOUND THEN RETURN false; END IF;
                        DELETE FROM bil.migration_receivable WHERE receivable_id = p_id;
                        RETURN true;
                    ELSIF p_kind = 'MANDATE' THEN
                        PERFORM 1 FROM bil.migration_mandate WHERE mandate_id = p_id AND first_used_at IS NULL FOR UPDATE;
                        IF NOT FOUND THEN RETURN false; END IF;
                        DELETE FROM bil.migration_mandate WHERE mandate_id = p_id;
                        RETURN true;
                    END IF;
                    RETURN false;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS bil.reverse_migrated(text, uuid);");

            migrationBuilder.DropTable(
                name: "migration_mandate",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "migration_receivable",
                schema: "bil");
        }
    }
}
