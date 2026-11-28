using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankReconciliation : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_statement",
                schema: "bil",
                columns: table => new
                {
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account = table.Column<string>(type: "text", nullable: false),
                    statement_ref = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    file_ref = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: false),
                    opening_balance = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    closing_balance = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    imported_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    imported_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_statement", x => x.statement_id);
                    table.CheckConstraint("ck_bank_statement_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_bank_statement_format", "format IN ('CAMT053', 'CSV')");
                    table.CheckConstraint("ck_bank_statement_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_bank_statement_lines", "line_count >= 0");
                    table.CheckConstraint("ck_bank_statement_period", "to_date >= from_date");
                });

            migrationBuilder.CreateTable(
                name: "bank_statement_line",
                schema: "bil",
                columns: table => new
                {
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    booking_date = table.Column<DateOnly>(type: "date", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: true),
                    end_to_end_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    matched_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    disbursement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    suspense_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_statement_line", x => x.line_id);
                    table.CheckConstraint("ck_bank_statement_line_amount", "amount > 0 AND matched_amount >= 0");
                    table.CheckConstraint("ck_bank_statement_line_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_bank_statement_line_direction", "direction IN ('CREDIT', 'DEBIT')");
                    table.CheckConstraint("ck_bank_statement_line_status", "status IN ('MATCHED', 'PARTIAL', 'SUSPENSE', 'UNMATCHED')");
                    table.ForeignKey(
                        name: "fk_bank_statement_line_disbursement",
                        column: x => x.disbursement_id,
                        principalSchema: "bil",
                        principalTable: "disbursement",
                        principalColumn: "disbursement_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_statement_line_receipt",
                        column: x => x.receipt_id,
                        principalSchema: "bil",
                        principalTable: "receipt",
                        principalColumn: "receipt_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_statement_line_statement",
                        column: x => x.statement_id,
                        principalSchema: "bil",
                        principalTable: "bank_statement",
                        principalColumn: "statement_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_bank_statement_content",
                schema: "bil",
                table: "bank_statement",
                columns: new[] { "legal_entity_id", "bank_account", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_bank_statement_ref",
                schema: "bil",
                table: "bank_statement",
                columns: new[] { "legal_entity_id", "bank_account", "statement_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bank_statement_line_disbursement",
                schema: "bil",
                table: "bank_statement_line",
                column: "disbursement_id");

            migrationBuilder.CreateIndex(
                name: "ix_bank_statement_line_receipt",
                schema: "bil",
                table: "bank_statement_line",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "ux_bank_statement_line_no",
                schema: "bil",
                table: "bank_statement_line",
                columns: new[] { "statement_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_statement_line",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "bank_statement",
                schema: "bil");
        }
    }
}
