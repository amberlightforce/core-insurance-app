using System;
using CoreIns.Modules.Finance.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LevyReturns : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "levy_return",
                schema: "fin",
                columns: table => new
                {
                    return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_code = table.Column<string>(type: "text", nullable: false),
                    levy_type = table.Column<string>(type: "text", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    supersedes_return_id = table.Column<Guid>(type: "uuid", nullable: true),
                    superseded_by_return_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    ledger_amount = table.Column<decimal>(type: "numeric(19,2)", nullable: false),
                    bil_amount = table.Column<decimal>(type: "numeric(19,2)", nullable: false),
                    adjustments_total = table.Column<decimal>(type: "numeric(19,2)", nullable: false),
                    net_payable = table.Column<decimal>(type: "numeric(19,2)", nullable: false),
                    entry_count = table.Column<int>(type: "integer", nullable: false),
                    recon_explanation = table.Column<string>(type: "text", nullable: true),
                    legal_status = table.Column<string>(type: "text", nullable: false),
                    provisional = table.Column<bool>(type: "boolean", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: true),
                    file_name = table.Column<string>(type: "text", nullable: true),
                    file_hash = table.Column<string>(type: "text", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_by = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    reviewed_by = table.Column<string>(type: "text", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    filed_by = table.Column<string>(type: "text", nullable: true),
                    filed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    filed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    filing_reference = table.Column<string>(type: "text", nullable: true),
                    disbursement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_status = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_levy_return", x => x.return_id);
                    table.CheckConstraint("ck_levy_return_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_levy_return_hash", "content_hash IS NULL OR content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_levy_return_period", "period_code ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                    table.CheckConstraint("ck_levy_return_status", "status IN ('DRAFT','REVIEWED','FILED','PAID')");
                    table.CheckConstraint("ck_levy_return_version", "version_no >= 1");
                });

            migrationBuilder.CreateTable(
                name: "levy_return_adjustment",
                schema: "fin",
                columns: table => new
                {
                    adjustment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,2)", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    evidence_ref = table.Column<string>(type: "text", nullable: true),
                    explains_reconciliation = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_levy_return_adjustment", x => x.adjustment_id);
                    table.CheckConstraint("ck_levy_return_adjustment_amount", "amount <> 0");
                    table.ForeignKey(
                        name: "fk_levy_return_adjustment_return",
                        column: x => x.return_id,
                        principalSchema: "fin",
                        principalTable: "levy_return",
                        principalColumn: "return_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_levy_return_period",
                schema: "fin",
                table: "levy_return",
                columns: new[] { "legal_entity_id", "period_code", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_levy_return_version",
                schema: "fin",
                table: "levy_return",
                columns: new[] { "legal_entity_id", "period_code", "levy_type", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_levy_return_adjustment_return",
                schema: "fin",
                table: "levy_return_adjustment",
                column: "return_id");

            migrationBuilder.Sql(LevyReturnSql.Triggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LevyReturnSql.DropTriggers);

            migrationBuilder.DropTable(
                name: "levy_return_adjustment",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "levy_return",
                schema: "fin");
        }
    }
}
