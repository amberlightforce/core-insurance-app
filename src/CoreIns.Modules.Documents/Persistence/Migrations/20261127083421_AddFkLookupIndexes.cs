using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFkLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_evidence_delivery",
                schema: "doc",
                table: "evidence",
                column: "delivery_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_archive",
                schema: "doc",
                table: "document",
                column: "archive_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_recipient_party",
                schema: "doc",
                table: "document",
                column: "recipient_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_delivery_recipient_party",
                schema: "doc",
                table: "delivery",
                column: "recipient_party_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_evidence_delivery",
                schema: "doc",
                table: "evidence");

            migrationBuilder.DropIndex(
                name: "ix_document_archive",
                schema: "doc",
                table: "document");

            migrationBuilder.DropIndex(
                name: "ix_document_recipient_party",
                schema: "doc",
                table: "document");

            migrationBuilder.DropIndex(
                name: "ix_delivery_recipient_party",
                schema: "doc",
                table: "delivery");
        }
    }
}
