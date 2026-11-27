using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFkLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_fiscal_document_counterparty_party",
                schema: "cmp",
                table: "fiscal_document",
                column: "counterparty_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_disclosure_log_dsar",
                schema: "cmp",
                table: "disclosure_log",
                column: "dsar_id");

            migrationBuilder.CreateIndex(
                name: "ix_complaint_complainant_party",
                schema: "cmp",
                table: "complaint",
                column: "complainant_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_bureau_handover_policy",
                schema: "cmp",
                table: "bureau_handover",
                column: "policy_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_fiscal_document_counterparty_party",
                schema: "cmp",
                table: "fiscal_document");

            migrationBuilder.DropIndex(
                name: "ix_disclosure_log_dsar",
                schema: "cmp",
                table: "disclosure_log");

            migrationBuilder.DropIndex(
                name: "ix_complaint_complainant_party",
                schema: "cmp",
                table: "complaint");

            migrationBuilder.DropIndex(
                name: "ix_bureau_handover_policy",
                schema: "cmp",
                table: "bureau_handover");
        }
    }
}
