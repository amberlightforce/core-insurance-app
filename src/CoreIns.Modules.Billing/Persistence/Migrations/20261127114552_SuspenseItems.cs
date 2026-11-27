using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SuspenseItems : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "suspense_item",
                schema: "bil",
                columns: table => new
                {
                    suspense_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    resolved_by = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suspense_item", x => x.suspense_item_id);
                    table.CheckConstraint("ck_suspense_item_amount", "amount > 0");
                    table.CheckConstraint("ck_suspense_item_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_suspense_item_state", "state IN ('OPEN', 'REFUND_PENDING', 'ALLOCATED', 'REFUNDED')");
                    table.ForeignKey(
                        name: "fk_suspense_item_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_suspense_item_receipt",
                        column: x => x.receipt_id,
                        principalSchema: "bil",
                        principalTable: "receipt",
                        principalColumn: "receipt_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_suspense_item_account",
                schema: "bil",
                table: "suspense_item",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_suspense_item_refund",
                schema: "bil",
                table: "suspense_item",
                column: "refund_id");

            migrationBuilder.CreateIndex(
                name: "ix_suspense_item_state",
                schema: "bil",
                table: "suspense_item",
                columns: new[] { "legal_entity_id", "state", "suspense_item_id" });

            migrationBuilder.CreateIndex(
                name: "ux_suspense_item_receipt",
                schema: "bil",
                table: "suspense_item",
                column: "receipt_id",
                unique: true);

            // BO-BIL-08: suspense ledger rules and account, the receipt freeze relaxed for transfers, backfill of the OPEN items.
            migrationBuilder.Sql(SuspenseDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SuspenseDatabaseSql.Down);

            migrationBuilder.DropTable(
                name: "suspense_item",
                schema: "bil");
        }
    }
}
