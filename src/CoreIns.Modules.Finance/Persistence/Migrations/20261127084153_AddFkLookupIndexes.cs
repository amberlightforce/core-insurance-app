using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFkLookupIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_journal_line_billing_account",
                schema: "fin",
                table: "journal_line",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_invoice",
                schema: "fin",
                table: "journal_line",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_policy_id",
                schema: "fin",
                table: "journal_line",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_event_billing_account",
                schema: "fin",
                table: "business_event",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_event_policy",
                schema: "fin",
                table: "business_event",
                column: "policy_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_journal_line_billing_account",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropIndex(
                name: "ix_journal_line_invoice",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropIndex(
                name: "ix_journal_line_policy_id",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropIndex(
                name: "ix_business_event_billing_account",
                schema: "fin",
                table: "business_event");

            migrationBuilder.DropIndex(
                name: "ix_business_event_policy",
                schema: "fin",
                table: "business_event");
        }
    }
}
