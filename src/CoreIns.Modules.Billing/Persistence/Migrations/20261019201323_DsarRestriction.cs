using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DsarRestriction : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dsar_restriction",
                schema: "bil",
                columns: table => new
                {
                    restriction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_class = table.Column<string>(type: "text", nullable: false),
                    legal_basis = table.Column<string>(type: "text", nullable: false),
                    retention_end = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    restricted_by = table.Column<string>(type: "text", nullable: false),
                    restricted_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dsar_restriction", x => x.restriction_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_dsar_restriction_subject_class",
                schema: "bil",
                table: "dsar_restriction",
                columns: new[] { "subject_party_id", "record_class" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dsar_restriction",
                schema: "bil");
        }
    }
}
