using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentLibraryIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_document_library",
                schema: "doc",
                table: "document",
                columns: new[] { "legal_entity_id", "created_at", "document_number" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_document_library_status",
                schema: "doc",
                table: "document",
                columns: new[] { "legal_entity_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_document_library_type",
                schema: "doc",
                table: "document",
                columns: new[] { "legal_entity_id", "template_key", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_document_library", schema: "doc", table: "document");

            migrationBuilder.DropIndex(name: "ix_document_library_status", schema: "doc", table: "document");

            migrationBuilder.DropIndex(name: "ix_document_library_type", schema: "doc", table: "document");
        }
    }
}
