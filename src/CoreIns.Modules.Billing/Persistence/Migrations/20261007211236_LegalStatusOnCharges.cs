using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LegalStatusOnCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legal_status",
                schema: "bil",
                table: "invoice_item",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "provisional",
                schema: "bil",
                table: "invoice_item",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "legal_status",
                schema: "bil",
                table: "charge",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "provisional",
                schema: "bil",
                table: "charge",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "legal_status",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropColumn(
                name: "provisional",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropColumn(
                name: "legal_status",
                schema: "bil",
                table: "charge");

            migrationBuilder.DropColumn(
                name: "provisional",
                schema: "bil",
                table: "charge");
        }
    }
}
