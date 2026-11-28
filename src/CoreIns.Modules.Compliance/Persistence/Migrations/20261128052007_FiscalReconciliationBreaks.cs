using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FiscalReconciliationBreaks : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fiscal_reconciliation_break",
                schema: "cmp",
                columns: table => new
                {
                    break_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fiscal_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    run_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fiscal_reconciliation_break", x => x.break_id);
                    table.CheckConstraint("ck_fiscal_reconciliation_break_kind", "kind IN ('REJECTED_BY_CHANNEL','PENDING_AT_CHANNEL','MARK_MISSING','CORRELATION_BROKEN')");
                    table.CheckConstraint("ck_fiscal_reconciliation_break_status", "status IN ('OPEN','RESOLVED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_reconciliation_break_status",
                schema: "cmp",
                table: "fiscal_reconciliation_break",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_reconciliation_break_doc_kind",
                schema: "cmp",
                table: "fiscal_reconciliation_break",
                columns: new[] { "legal_entity_id", "fiscal_document_id", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fiscal_reconciliation_break",
                schema: "cmp");
        }
    }
}
