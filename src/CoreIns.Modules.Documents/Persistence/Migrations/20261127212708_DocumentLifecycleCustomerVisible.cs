using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentLifecycleCustomerVisible : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "customer_visible",
                schema: "doc",
                table: "document_type",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "hidden",
                schema: "doc",
                table: "document",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "hidden_reason",
                schema: "doc",
                table: "document",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "superseded_by",
                schema: "doc",
                table: "document",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "withdrawn_reason",
                schema: "doc",
                table: "document",
                type: "text",
                nullable: true);

            // The letters the catalogue seeded so far are all customer correspondence; every later type starts internal (false) until set.
            migrationBuilder.Sql(
                "UPDATE doc.document_type SET customer_visible = true WHERE document_type IN ('DT-NONPAY-NOTICE','DT-WITHDRAWAL-ACK','DT-CANCELLATION-NOTICE','DT-REFUND-ADVICE','DT-NONRENEWAL-NOTICE');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "customer_visible",
                schema: "doc",
                table: "document_type");

            migrationBuilder.DropColumn(
                name: "hidden",
                schema: "doc",
                table: "document");

            migrationBuilder.DropColumn(
                name: "hidden_reason",
                schema: "doc",
                table: "document");

            migrationBuilder.DropColumn(
                name: "superseded_by",
                schema: "doc",
                table: "document");

            migrationBuilder.DropColumn(
                name: "withdrawn_reason",
                schema: "doc",
                table: "document");
        }
    }
}
