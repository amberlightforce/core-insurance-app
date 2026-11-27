using System;
using Microsoft.EntityFrameworkCore.Migrations;
using CoreIns.Modules.Reinsurance.Persistence;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CashCalls : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cash_call",
                schema: "ri",
                columns: table => new
                {
                    cash_call_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    threshold = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    raised_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    raised_by = table.Column<string>(type: "text", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    sent_by = table.Column<string>(type: "text", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    cancelled_by = table.Column<string>(type: "text", nullable: true),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    settled_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_call", x => x.cash_call_id);
                    table.CheckConstraint("ck_cash_call_status", "status IN ('RAISED', 'SENT', 'PARTIALLY_SETTLED', 'SETTLED', 'CANCELLED')");
                    table.CheckConstraint("ck_cash_call_total", "total > 0");
                    table.ForeignKey(
                        name: "fk_cash_call_contract",
                        column: x => x.contract_id,
                        principalSchema: "ri",
                        principalTable: "contract",
                        principalColumn: "contract_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_call_line",
                schema: "ri",
                columns: table => new
                {
                    cash_call_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reinsurer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    received = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_call_line", x => new { x.cash_call_id, x.reinsurer_party_id });
                    table.CheckConstraint("ck_cash_call_line_received", "received >= 0 AND received <= amount");
                    table.ForeignKey(
                        name: "fk_cash_call_line_call",
                        column: x => x.cash_call_id,
                        principalSchema: "ri",
                        principalTable: "cash_call",
                        principalColumn: "cash_call_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_call_receipt",
                schema: "ri",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cash_call_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reinsurer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    bank_reference = table.Column<string>(type: "text", nullable: false),
                    received_on = table.Column<DateOnly>(type: "date", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_call_receipt", x => x.receipt_id);
                    table.CheckConstraint("ck_cash_call_receipt_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_cash_call_receipt_call",
                        column: x => x.cash_call_id,
                        principalSchema: "ri",
                        principalTable: "cash_call",
                        principalColumn: "cash_call_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cash_call_status",
                schema: "ri",
                table: "cash_call",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_cash_call_live",
                schema: "ri",
                table: "cash_call",
                columns: new[] { "contract_id", "source_key" },
                unique: true,
                filter: "status <> 'CANCELLED'");

            migrationBuilder.CreateIndex(
                name: "ix_cash_call_receipt_call",
                schema: "ri",
                table: "cash_call_receipt",
                column: "cash_call_id");

            migrationBuilder.CreateIndex(
                name: "ux_cash_call_receipt_reference",
                schema: "ri",
                table: "cash_call_receipt",
                columns: new[] { "legal_entity_id", "reinsurer_party_id", "bank_reference" },
                unique: true);
            migrationBuilder.Sql(CashCallDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CashCallDatabaseSql.Down);
            migrationBuilder.DropTable(
                name: "cash_call_line",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "cash_call_receipt",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "cash_call",
                schema: "ri");
        }
    }
}
