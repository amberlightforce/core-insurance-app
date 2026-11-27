using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstalmentInvoices : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_invoice_transaction",
                schema: "bil",
                table: "invoice");

            migrationBuilder.AddColumn<int>(
                name: "instalment_count",
                schema: "bil",
                table: "invoice",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "instalment_no",
                schema: "bil",
                table: "invoice",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ux_invoice_transaction",
                schema: "bil",
                table: "invoice",
                columns: new[] { "transaction_id", "kind", "instalment_no" },
                unique: true,
                filter: "kind = 'INVOICE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_invoice_transaction",
                schema: "bil",
                table: "invoice");

            migrationBuilder.DropColumn(
                name: "instalment_count",
                schema: "bil",
                table: "invoice");

            migrationBuilder.DropColumn(
                name: "instalment_no",
                schema: "bil",
                table: "invoice");

            migrationBuilder.CreateIndex(
                name: "ux_invoice_transaction",
                schema: "bil",
                table: "invoice",
                columns: new[] { "transaction_id", "kind" },
                unique: true,
                filter: "kind = 'INVOICE'");
        }
    }
}
