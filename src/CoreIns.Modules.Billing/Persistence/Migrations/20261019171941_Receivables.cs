using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Receivables : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_billing_account_active_payer",
                schema: "bil",
                table: "billing_account");

            migrationBuilder.AddColumn<Guid>(
                name: "counterparty_party_id",
                schema: "bil",
                table: "ledger_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "recovery_id",
                schema: "bil",
                table: "ledger_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "statement_ref",
                schema: "bil",
                table: "ledger_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lines",
                schema: "bil",
                table: "disbursement",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "release_approval_request_id",
                schema: "bil",
                table: "disbursement",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "release_approved_by",
                schema: "bil",
                table: "disbursement",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "statement_ref",
                schema: "bil",
                table: "disbursement",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "account_type",
                schema: "bil",
                table: "billing_account",
                type: "text",
                nullable: false,
                defaultValue: "PREMIUM");

            migrationBuilder.CreateTable(
                name: "receivable",
                schema: "bil",
                columns: table => new
                {
                    receivable_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    counterparty_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recovery_id = table.Column<Guid>(type: "uuid", nullable: true),
                    statement_ref = table.Column<string>(type: "text", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    payment_reference = table.Column<string>(type: "text", nullable: false),
                    registered_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    registered_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receivable", x => x.receivable_id);
                    table.CheckConstraint("ck_receivable_amount", "amount > 0 AND amount = round(amount, 2)");
                    table.CheckConstraint("ck_receivable_currency", "currency = 'EUR'");
                    table.CheckConstraint("ck_receivable_source", "(source_type = 'CLM_CLAIM_PAYMENT' AND purpose IN ('SALVAGE','SUBROGATION') AND claim_id IS NOT NULL AND recovery_id IS NOT NULL AND statement_ref IS NULL) OR (source_type = 'FS_CLEARING' AND purpose = 'FS_NET' AND statement_ref IS NOT NULL AND claim_id IS NULL AND recovery_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_receivable_billing_account_billing_account_id",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receivable_allocation",
                schema: "bil",
                columns: table => new
                {
                    allocation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receivable_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    allocated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receivable_allocation", x => x.allocation_id);
                    table.CheckConstraint("ck_receivable_allocation_amount", "amount > 0 AND amount = round(amount, 2)");
                    table.ForeignKey(
                        name: "FK_receivable_allocation_receipt_receipt_id",
                        column: x => x.receipt_id,
                        principalSchema: "bil",
                        principalTable: "receipt",
                        principalColumn: "receipt_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_receivable_allocation_receivable_receivable_id",
                        column: x => x.receivable_id,
                        principalSchema: "bil",
                        principalTable: "receivable",
                        principalColumn: "receivable_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_billing_account_active_payer",
                schema: "bil",
                table: "billing_account",
                columns: new[] { "legal_entity_id", "payer_party_id", "currency", "account_type" },
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "IX_receivable_billing_account_id",
                schema: "bil",
                table: "receivable",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_receivable_legal_entity_id_source_type_source_id_purpose_co~",
                schema: "bil",
                table: "receivable",
                columns: new[] { "legal_entity_id", "source_type", "source_id", "purpose", "counterparty_party_id", "amount" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receivable_payment_reference",
                schema: "bil",
                table: "receivable",
                column: "payment_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receivable_allocation_receipt_id_receivable_id",
                schema: "bil",
                table: "receivable_allocation",
                columns: new[] { "receipt_id", "receivable_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_receivable_allocation_receivable_id",
                schema: "bil",
                table: "receivable_allocation",
                column: "receivable_id");

            migrationBuilder.Sql(ReceivableDatabaseSql.Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_allocation_shared_receipt ON bil.allocation;
                DROP FUNCTION IF EXISTS bil.guard_shared_receipt();
                DELETE FROM bil.ledger_rule WHERE rule_id IN ('BLR-RECV-CLM','BLR-RECV-FS','BLR-COLLECT-CLM','BLR-COLLECT-FS');
                DELETE FROM bil.ledger_account WHERE account_code = 'LA-28';
                """);

            migrationBuilder.DropTable(
                name: "receivable_allocation",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "receivable",
                schema: "bil");

            migrationBuilder.DropIndex(
                name: "ux_billing_account_active_payer",
                schema: "bil",
                table: "billing_account");

            migrationBuilder.DropColumn(
                name: "counterparty_party_id",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "recovery_id",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "statement_ref",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "lines",
                schema: "bil",
                table: "disbursement");

            migrationBuilder.DropColumn(
                name: "release_approval_request_id",
                schema: "bil",
                table: "disbursement");

            migrationBuilder.DropColumn(
                name: "release_approved_by",
                schema: "bil",
                table: "disbursement");

            migrationBuilder.DropColumn(
                name: "statement_ref",
                schema: "bil",
                table: "disbursement");

            migrationBuilder.DropColumn(
                name: "account_type",
                schema: "bil",
                table: "billing_account");

            migrationBuilder.CreateIndex(
                name: "ux_billing_account_active_payer",
                schema: "bil",
                table: "billing_account",
                columns: new[] { "legal_entity_id", "payer_party_id", "currency" },
                unique: true,
                filter: "status = 'ACTIVE'");
        }
    }
}
