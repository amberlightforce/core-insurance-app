using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayeeAccountsAndDisbursements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "claim_id",
                schema: "bil",
                table: "ledger_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "disbursement_id",
                schema: "bil",
                table: "ledger_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_id",
                schema: "bil",
                table: "ledger_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_type",
                schema: "bil",
                table: "ledger_line",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "billing_account_id",
                schema: "bil",
                table: "ledger_entry",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "disbursement_id",
                schema: "bil",
                table: "ledger_entry",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payee_account",
                schema: "bil",
                columns: table => new
                {
                    payee_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    iban_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    iban_blind_index = table.Column<string>(type: "text", nullable: false),
                    iban_last4 = table.Column<string>(type: "char(4)", nullable: false),
                    holder_name = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    evidence_ref = table.Column<string>(type: "text", nullable: true),
                    verification_status = table.Column<string>(type: "text", nullable: false),
                    vop_result = table.Column<string>(type: "text", nullable: true),
                    vop_suggested_name = table.Column<string>(type: "text", nullable: true),
                    vop_checked_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    cooling_off_until = table.Column<DateOnly>(type: "date", nullable: false),
                    is_change = table.Column<bool>(type: "boolean", nullable: false),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payee_account", x => x.payee_account_id);
                    table.CheckConstraint("ck_payee_account_last4", "iban_last4 ~ '^[A-Z0-9]{4}$'");
                    table.CheckConstraint("ck_payee_account_purpose", "purpose ~ '^[A-Z][A-Z_]{1,31}$'");
                    table.CheckConstraint("ck_payee_account_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_payee_account_status", "status IN ('ACTIVE', 'SUPERSEDED', 'REVOKED')");
                    table.CheckConstraint("ck_payee_account_valid", "valid_to IS NULL OR valid_to >= valid_from");
                    table.CheckConstraint("ck_payee_account_verification", "verification_status IN ('UNVERIFIED', 'VOP_MATCHED', 'VOP_CLOSE_MATCH', 'VOP_NO_MATCH', 'VOP_NOT_AVAILABLE', 'CONFIRMED')");
                    table.CheckConstraint("ck_payee_account_vop", "vop_result IS NULL OR vop_result IN ('MATCH', 'CLOSE_MATCH', 'NO_MATCH', 'NOT_AVAILABLE')");
                });

            migrationBuilder.CreateTable(
                name: "disbursement",
                schema: "bil",
                columns: table => new
                {
                    disbursement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    disbursement_number = table.Column<string>(type: "text", nullable: false),
                    source_module = table.Column<string>(type: "text", nullable: false),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payee_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payee_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    requested_value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    approval_evidence_ref = table.Column<string>(type: "text", nullable: false),
                    approval_content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    purpose_text = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    screening_result = table.Column<string>(type: "text", nullable: false),
                    screening_list_versions = table.Column<string>(type: "text", nullable: true),
                    screened_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    vop_result = table.Column<string>(type: "text", nullable: false),
                    bank_reference = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    released_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    issued_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    cleared_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    release_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    clear_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disbursement", x => x.disbursement_id);
                    table.CheckConstraint("ck_disbursement_amount", "amount > 0");
                    table.CheckConstraint("ck_disbursement_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_disbursement_hash", "approval_content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_disbursement_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_disbursement_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_disbursement_state", "state IN ('REQUESTED', 'PENDING_APPROVAL', 'APPROVED', 'RELEASED', 'ISSUED', 'CLEARED', 'REJECTED', 'STOPPED', 'VOIDED', 'RETURNED')");
                    table.ForeignKey(
                        name: "fk_disbursement_payee_account",
                        column: x => x.payee_account_id,
                        principalSchema: "bil",
                        principalTable: "payee_account",
                        principalColumn: "payee_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entry_disbursement",
                schema: "bil",
                table: "ledger_entry",
                column: "disbursement_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ledger_entry_owner",
                schema: "bil",
                table: "ledger_entry",
                sql: "(billing_account_id IS NULL) <> (disbursement_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_disbursement_claim",
                schema: "bil",
                table: "disbursement",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "IX_disbursement_payee_account_id",
                schema: "bil",
                table: "disbursement",
                column: "payee_account_id");

            migrationBuilder.CreateIndex(
                name: "ux_disbursement_duplicate_key",
                schema: "bil",
                table: "disbursement",
                columns: new[] { "legal_entity_id", "payee_account_id", "amount", "currency", "source_type", "claim_id" },
                unique: true,
                filter: "state NOT IN ('REJECTED', 'STOPPED', 'VOIDED', 'RETURNED') AND claim_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_disbursement_number",
                schema: "bil",
                table: "disbursement",
                columns: new[] { "legal_entity_id", "disbursement_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_disbursement_source",
                schema: "bil",
                table: "disbursement",
                columns: new[] { "legal_entity_id", "source_type", "source_id" },
                unique: true,
                filter: "state NOT IN ('REJECTED', 'STOPPED', 'VOIDED', 'RETURNED')");

            migrationBuilder.CreateIndex(
                name: "ix_payee_account_iban_index",
                schema: "bil",
                table: "payee_account",
                column: "iban_blind_index");

            migrationBuilder.CreateIndex(
                name: "ux_payee_account_active",
                schema: "bil",
                table: "payee_account",
                columns: new[] { "legal_entity_id", "party_id", "purpose" },
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.Sql(BillingDatabaseSql.Disbursements);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BillingDatabaseSql.DisbursementsDown);

            migrationBuilder.DropTable(
                name: "disbursement",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "payee_account",
                schema: "bil");

            migrationBuilder.DropIndex(
                name: "ix_ledger_entry_disbursement",
                schema: "bil",
                table: "ledger_entry");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ledger_entry_owner",
                schema: "bil",
                table: "ledger_entry");

            migrationBuilder.DropColumn(
                name: "claim_id",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "disbursement_id",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "source_id",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "source_type",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "disbursement_id",
                schema: "bil",
                table: "ledger_entry");

            migrationBuilder.AlterColumn<Guid>(
                name: "billing_account_id",
                schema: "bil",
                table: "ledger_entry",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
