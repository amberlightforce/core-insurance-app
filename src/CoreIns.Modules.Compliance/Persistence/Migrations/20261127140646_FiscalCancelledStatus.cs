using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FiscalCancelledStatus : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_fiscal_document_status",
                schema: "cmp",
                table: "fiscal_document");

            migrationBuilder.AddCheckConstraint(
                name: "ck_fiscal_document_status",
                schema: "cmp",
                table: "fiscal_document",
                sql: "status IN ('PENDING', 'REGISTERED', 'REJECTED', 'CANCELLED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_fiscal_document_status",
                schema: "cmp",
                table: "fiscal_document");

            migrationBuilder.AddCheckConstraint(
                name: "ck_fiscal_document_status",
                schema: "cmp",
                table: "fiscal_document",
                sql: "status IN ('PENDING', 'REGISTERED', 'REJECTED')");
        }
    }
}
