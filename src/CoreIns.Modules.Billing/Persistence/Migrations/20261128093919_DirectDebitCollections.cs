using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DirectDebitCollections : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DirectDebitSql.Up);

            migrationBuilder.DropCheckConstraint(
                name: "ck_suspense_item_state",
                schema: "bil",
                table: "suspense_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_receipt_method",
                schema: "bil",
                table: "receipt");

            migrationBuilder.CreateTable(
                name: "dd_collection",
                schema: "bil",
                columns: table => new
                {
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    file_reference = table.Column<string>(type: "text", nullable: false),
                    file_sha256 = table.Column<string>(type: "text", nullable: false),
                    collection_date = table.Column<DateOnly>(type: "date", nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dd_collection", x => x.collection_id);
                    table.CheckConstraint("ck_dd_collection_count", "item_count > 0 AND total_amount > 0");
                    table.CheckConstraint("ck_dd_collection_currency", "currency ~ '^[A-Z]{3}$'");
                });

            migrationBuilder.CreateTable(
                name: "dd_item",
                schema: "bil",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mandate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<Guid>(type: "uuid", nullable: false),
                    end_to_end_id = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    sequence_type = table.Column<string>(type: "text", nullable: false),
                    collection_date = table.Column<DateOnly>(type: "date", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    settled_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dd_item", x => x.item_id);
                    table.CheckConstraint("ck_dd_item_amount", "amount > 0");
                    table.CheckConstraint("ck_dd_item_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_dd_item_end_to_end", "char_length(end_to_end_id) BETWEEN 1 AND 35");
                    table.CheckConstraint("ck_dd_item_receipt", "(state IN ('COLLECTED', 'RETURNED')) = (receipt_id IS NOT NULL)");
                    table.CheckConstraint("ck_dd_item_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_dd_item_sequence", "sequence_type IN ('FRST', 'RCUR')");
                    table.CheckConstraint("ck_dd_item_state", "state IN ('SUBMITTED', 'COLLECTED', 'REJECTED', 'RETURNED')");
                    table.ForeignKey(
                        name: "fk_dd_item_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dd_item_collection",
                        column: x => x.collection_id,
                        principalSchema: "bil",
                        principalTable: "dd_collection",
                        principalColumn: "collection_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dd_item_invoice",
                        column: x => x.invoice_id,
                        principalSchema: "bil",
                        principalTable: "invoice",
                        principalColumn: "invoice_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dd_item_mandate",
                        column: x => x.mandate_id,
                        principalSchema: "bil",
                        principalTable: "mandate",
                        principalColumn: "mandate_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_suspense_item_state",
                schema: "bil",
                table: "suspense_item",
                sql: "state IN ('OPEN', 'REFUND_PENDING', 'ALLOCATED', 'REFUNDED', 'RETURNED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_receipt_method",
                schema: "bil",
                table: "receipt",
                sql: "method IN ('BANK_TRANSFER', 'CASHIER', 'DIRECT_DEBIT')");

            migrationBuilder.CreateIndex(
                name: "ux_dd_collection_message",
                schema: "bil",
                table: "dd_collection",
                columns: new[] { "legal_entity_id", "message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dd_item_billing_account_id",
                schema: "bil",
                table: "dd_item",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_dd_item_collection",
                schema: "bil",
                table: "dd_item",
                column: "collection_id");

            migrationBuilder.CreateIndex(
                name: "IX_dd_item_mandate_id",
                schema: "bil",
                table: "dd_item",
                column: "mandate_id");

            migrationBuilder.CreateIndex(
                name: "ix_dd_item_state",
                schema: "bil",
                table: "dd_item",
                columns: new[] { "legal_entity_id", "state", "collection_date" });

            migrationBuilder.CreateIndex(
                name: "ux_dd_item_end_to_end",
                schema: "bil",
                table: "dd_item",
                columns: new[] { "legal_entity_id", "end_to_end_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dd_item_invoice_live",
                schema: "bil",
                table: "dd_item",
                column: "invoice_id",
                unique: true,
                filter: "state IN ('SUBMITTED', 'COLLECTED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DirectDebitSql.Down);

            migrationBuilder.DropTable(
                name: "dd_item",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "dd_collection",
                schema: "bil");

            migrationBuilder.DropCheckConstraint(
                name: "ck_suspense_item_state",
                schema: "bil",
                table: "suspense_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_receipt_method",
                schema: "bil",
                table: "receipt");

            migrationBuilder.AddCheckConstraint(
                name: "ck_suspense_item_state",
                schema: "bil",
                table: "suspense_item",
                sql: "state IN ('OPEN', 'REFUND_PENDING', 'ALLOCATED', 'REFUNDED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_receipt_method",
                schema: "bil",
                table: "receipt",
                sql: "method IN ('BANK_TRANSFER', 'CASHIER')");
        }
    }
}
