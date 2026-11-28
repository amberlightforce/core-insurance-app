using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatEventTracking : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cat_event_code",
                schema: "clm",
                table: "claim",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "milestone",
                schema: "clm",
                table: "claim",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "milestone_at",
                schema: "clm",
                table: "claim",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cat_event",
                schema: "clm",
                columns: table => new
                {
                    cat_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cat_code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    perils = table.Column<string[]>(type: "text[]", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    window_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    window_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    close_reason = table.Column<string>(type: "text", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cat_event", x => x.cat_event_id);
                    table.CheckConstraint("ck_cat_event_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_cat_event_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_cat_event_status", "status IN ('OPEN', 'CLOSED')");
                    table.CheckConstraint("ck_cat_event_window", "window_to IS NULL OR window_to > window_from");
                });

            migrationBuilder.CreateIndex(
                name: "ix_claim_cat_event",
                schema: "clm",
                table: "claim",
                columns: new[] { "legal_entity_id", "cat_event_code" },
                filter: "cat_event_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_cat_event_code",
                schema: "clm",
                table: "cat_event",
                columns: new[] { "legal_entity_id", "cat_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cat_event",
                schema: "clm");

            migrationBuilder.DropIndex(
                name: "ix_claim_cat_event",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "cat_event_code",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "milestone",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "milestone_at",
                schema: "clm",
                table: "claim");
        }
    }
}
