using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DisbursementBatches : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // BO-BIL-10: ledger rules of the bank release and its reversal for the four non-claim sources (data).
            migrationBuilder.Sql(DisbursementBatchSql.Rules);

            migrationBuilder.CreateTable(
                name: "disbursement_batch",
                schema: "bil",
                columns: table => new
                {
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    bank_account = table.Column<string>(type: "text", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    file_hash = table.Column<string>(type: "char(64)", nullable: false),
                    file_ref = table.Column<string>(type: "text", nullable: false),
                    prepared_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    prepared_by = table.Column<string>(type: "text", nullable: false),
                    release_requested_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    release_requested_by = table.Column<string>(type: "text", nullable: true),
                    released_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    released_by = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disbursement_batch", x => x.batch_id);
                    table.CheckConstraint("ck_disbursement_batch_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_disbursement_batch_hash", "file_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_disbursement_batch_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_disbursement_batch_state", "state IN ('PREPARED', 'RELEASE_REQUESTED', 'RELEASED')");
                    table.CheckConstraint("ck_disbursement_batch_total", "total_amount > 0 AND item_count > 0");
                });

            migrationBuilder.CreateTable(
                name: "disbursement_batch_item",
                schema: "bil",
                columns: table => new
                {
                    disbursement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    dropped = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disbursement_batch_item", x => x.disbursement_id);
                    table.CheckConstraint("ck_disbursement_batch_item_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_disbursement_batch_item_batch",
                        column: x => x.batch_id,
                        principalSchema: "bil",
                        principalTable: "disbursement_batch",
                        principalColumn: "batch_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_disbursement_batch_item_disbursement",
                        column: x => x.disbursement_id,
                        principalSchema: "bil",
                        principalTable: "disbursement",
                        principalColumn: "disbursement_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_disbursement_batch_file",
                schema: "bil",
                table: "disbursement_batch",
                columns: new[] { "legal_entity_id", "file_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_disbursement_batch_item_batch",
                schema: "bil",
                table: "disbursement_batch_item",
                column: "batch_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DisbursementBatchSql.RulesDown);

            migrationBuilder.DropTable(
                name: "disbursement_batch_item",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "disbursement_batch",
                schema: "bil");
        }
    }
}
