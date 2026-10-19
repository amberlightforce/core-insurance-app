using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LegacyContinuity : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bureau_handover",
                schema: "cmp",
                columns: table => new
                {
                    handover_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_ref = table.Column<string>(type: "text", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    handover_date = table.Column<DateOnly>(type: "date", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bureau_handover", x => x.handover_id);
                });

            migrationBuilder.CreateTable(
                name: "legacy_fiscal_mark",
                schema: "cmp",
                columns: table => new
                {
                    mark_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<string>(type: "text", nullable: false),
                    legacy_number = table.Column<long>(type: "bigint", nullable: false),
                    mark = table.Column<string>(type: "text", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legacy_fiscal_mark", x => x.mark_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_bureau_handover_vehicle",
                schema: "cmp",
                table: "bureau_handover",
                columns: new[] { "legal_entity_id", "vehicle_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_legacy_fiscal_mark_mark",
                schema: "cmp",
                table: "legacy_fiscal_mark",
                columns: new[] { "legal_entity_id", "mark" });

            migrationBuilder.CreateIndex(
                name: "ux_legacy_fiscal_mark_number",
                schema: "cmp",
                table: "legacy_fiscal_mark",
                columns: new[] { "legal_entity_id", "series_id", "legacy_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bureau_handover",
                schema: "cmp");

            migrationBuilder.DropTable(
                name: "legacy_fiscal_mark",
                schema: "cmp");
        }
    }
}
