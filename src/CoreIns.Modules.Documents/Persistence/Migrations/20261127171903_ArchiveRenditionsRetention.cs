using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveRenditionsRetention : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_archive_id",
                schema: "doc",
                table: "archive",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rendition_kind",
                schema: "doc",
                table: "archive",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "retention_class",
                schema: "doc",
                table: "archive",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_archive_created",
                schema: "doc",
                table: "archive",
                columns: new[] { "legal_entity_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_archive_parent",
                schema: "doc",
                table: "archive",
                column: "parent_archive_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_archive_created",
                schema: "doc",
                table: "archive");

            migrationBuilder.DropIndex(
                name: "ix_archive_parent",
                schema: "doc",
                table: "archive");

            migrationBuilder.DropColumn(
                name: "parent_archive_id",
                schema: "doc",
                table: "archive");

            migrationBuilder.DropColumn(
                name: "rendition_kind",
                schema: "doc",
                table: "archive");

            migrationBuilder.DropColumn(
                name: "retention_class",
                schema: "doc",
                table: "archive");
        }
    }
}
