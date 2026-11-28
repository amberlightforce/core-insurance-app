using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WriteOffsAndTransfers : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "write_off",
                schema: "bil",
                columns: table => new
                {
                    write_off_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    proposed_by = table.Column<string>(type: "text", nullable: false),
                    proposed_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_comment = table.Column<string>(type: "text", nullable: true),
                    authority_check_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tolerance_limit = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    posted_by = table.Column<string>(type: "text", nullable: true),
                    posted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ledger_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_write_off", x => x.write_off_id);
                    table.CheckConstraint("ck_write_off_amount", "amount > 0");
                    table.CheckConstraint("ck_write_off_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_write_off_decided", "state = 'PROPOSED' OR decided_by IS NOT NULL");
                    table.CheckConstraint("ck_write_off_kind", "kind IN ('MANUAL', 'TOLERANCE')");
                    table.CheckConstraint("ck_write_off_posted", "(state = 'POSTED') = (ledger_entry_id IS NOT NULL)");
                    table.CheckConstraint("ck_write_off_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_write_off_state", "state IN ('PROPOSED', 'APPROVED', 'REJECTED', 'POSTED')");
                    table.ForeignKey(
                        name: "fk_write_off_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_write_off_invoice",
                        column: x => x.invoice_id,
                        principalSchema: "bil",
                        principalTable: "invoice",
                        principalColumn: "invoice_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "write_off_line",
                schema: "bil",
                columns: table => new
                {
                    write_off_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    write_off_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_write_off_line", x => x.write_off_line_id);
                    table.CheckConstraint("ck_write_off_line_amount", "amount > 0");
                    table.CheckConstraint("ck_write_off_line_currency", "currency ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "fk_write_off_line_invoice",
                        column: x => x.invoice_id,
                        principalSchema: "bil",
                        principalTable: "invoice",
                        principalColumn: "invoice_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_write_off_line_write_off",
                        column: x => x.write_off_id,
                        principalSchema: "bil",
                        principalTable: "write_off",
                        principalColumn: "write_off_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_write_off_account",
                schema: "bil",
                table: "write_off",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_write_off_invoice",
                schema: "bil",
                table: "write_off",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_write_off_state",
                schema: "bil",
                table: "write_off",
                columns: new[] { "legal_entity_id", "state", "proposed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_write_off_line_invoice_id",
                schema: "bil",
                table: "write_off_line",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_write_off_line_item",
                schema: "bil",
                table: "write_off_line",
                column: "invoice_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_write_off_line_item",
                schema: "bil",
                table: "write_off_line",
                columns: new[] { "write_off_id", "invoice_item_id" },
                unique: true);

            migrationBuilder.Sql(WriteOffSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(WriteOffSql.Down);

            migrationBuilder.DropTable(
                name: "write_off_line",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "write_off",
                schema: "bil");
        }
    }
}
