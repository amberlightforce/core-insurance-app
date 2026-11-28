using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MandatesAndInstruments : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_instrument",
                schema: "bil",
                columns: table => new
                {
                    instrument_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    iban_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    iban_blind_index = table.Column<string>(type: "text", nullable: false),
                    iban_last4 = table.Column<string>(type: "char(4)", nullable: false),
                    holder_name = table.Column<string>(type: "text", nullable: false),
                    vop_result = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    removed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_instrument", x => x.instrument_id);
                    table.CheckConstraint("ck_payment_instrument_kind", "kind = 'BANK_ACCOUNT'");
                    table.CheckConstraint("ck_payment_instrument_last4", "iban_last4 ~ '^[A-Z0-9]{4}$'");
                    table.CheckConstraint("ck_payment_instrument_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_payment_instrument_status", "status IN ('ACTIVE', 'REMOVED')");
                    table.CheckConstraint("ck_payment_instrument_vop", "vop_result IS NULL OR vop_result IN ('MATCH', 'CLOSE_MATCH', 'NO_MATCH', 'NOT_AVAILABLE')");
                });

            migrationBuilder.CreateTable(
                name: "mandate",
                schema: "bil",
                columns: table => new
                {
                    mandate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mandate_reference = table.Column<string>(type: "text", nullable: false),
                    scheme = table.Column<string>(type: "text", nullable: false),
                    sequence_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    iban_masked = table.Column<string>(type: "text", nullable: true),
                    signature_date = table.Column<DateOnly>(type: "date", nullable: true),
                    signature_evidence_id = table.Column<string>(type: "text", nullable: true),
                    amendment_count = table.Column<int>(type: "integer", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    first_used_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    cancelled_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mandate", x => x.mandate_id);
                    table.CheckConstraint("ck_mandate_origin", "origin IN ('NATIVE', 'MIGRATION')");
                    table.CheckConstraint("ck_mandate_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_mandate_reference", "char_length(mandate_reference) BETWEEN 1 AND 35");
                    table.CheckConstraint("ck_mandate_scheme", "scheme = 'SEPA_CORE'");
                    table.CheckConstraint("ck_mandate_sequence", "sequence_type IN ('FRST', 'RCUR', 'OOFF', 'FNAL')");
                    table.CheckConstraint("ck_mandate_signed", "status <> 'ACTIVE' OR (signature_date IS NOT NULL AND (origin = 'MIGRATION' OR signature_evidence_id IS NOT NULL))");
                    table.CheckConstraint("ck_mandate_status", "status IN ('DRAFT', 'ACTIVE', 'CANCELLED')");
                    table.ForeignKey(
                        name: "fk_mandate_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mandate_instrument",
                        column: x => x.instrument_id,
                        principalSchema: "bil",
                        principalTable: "payment_instrument",
                        principalColumn: "instrument_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mandate_account",
                schema: "bil",
                table: "mandate",
                columns: new[] { "legal_entity_id", "billing_account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_mandate_billing_account_id",
                schema: "bil",
                table: "mandate",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_mandate_instrument_id",
                schema: "bil",
                table: "mandate",
                column: "instrument_id");

            migrationBuilder.CreateIndex(
                name: "ux_mandate_live_account",
                schema: "bil",
                table: "mandate",
                columns: new[] { "legal_entity_id", "billing_account_id" },
                unique: true,
                filter: "origin = 'NATIVE' AND status IN ('DRAFT', 'ACTIVE')");

            migrationBuilder.CreateIndex(
                name: "ux_mandate_reference",
                schema: "bil",
                table: "mandate",
                columns: new[] { "legal_entity_id", "mandate_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_instrument_party",
                schema: "bil",
                table: "payment_instrument",
                columns: new[] { "legal_entity_id", "party_id" });

            migrationBuilder.CreateIndex(
                name: "ux_payment_instrument_active",
                schema: "bil",
                table: "payment_instrument",
                columns: new[] { "legal_entity_id", "party_id", "iban_blind_index" },
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION bil.reverse_migrated(p_kind text, p_id uuid) RETURNS boolean
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
                        DELETE FROM bil.mandate WHERE mandate_id = p_id AND origin = 'MIGRATION';
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

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION bil.reverse_migrated(p_kind text, p_id uuid) RETURNS boolean
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

            migrationBuilder.DropTable(
                name: "mandate",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "payment_instrument",
                schema: "bil");
        }
    }
}
