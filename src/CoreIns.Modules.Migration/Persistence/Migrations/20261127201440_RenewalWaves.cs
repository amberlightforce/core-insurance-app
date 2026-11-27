using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Migration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenewalWaves : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mapping_content_hash",
                schema: "mig",
                table: "batch",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "mapping_spec_id",
                schema: "mig",
                table: "batch",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "wave",
                schema: "mig",
                columns: table => new
                {
                    wave_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    term_end_field = table.Column<string>(type: "text", nullable: false),
                    window_from = table.Column<DateOnly>(type: "date", nullable: false),
                    window_to = table.Column<DateOnly>(type: "date", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    selected_count = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    closed_by = table.Column<string>(type: "text", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wave", x => x.wave_id);
                    table.CheckConstraint("ck_wave_status", "status IN ('PLANNED','IN_PROGRESS','CLOSED')");
                });

            migrationBuilder.CreateTable(
                name: "wave_term",
                schema: "mig",
                columns: table => new
                {
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wave_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    legacy_key = table.Column<string>(type: "text", nullable: false),
                    term_end = table.Column<DateOnly>(type: "date", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    core_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wave_term", x => x.term_id);
                    table.CheckConstraint("ck_wave_term_state", "state IN ('PLANNED','OFFERED','CONVERTED','EXCLUDED','WITHDRAWN','NOT_CONVERTED')");
                    table.ForeignKey(
                        name: "fk_wave_term_wave",
                        column: x => x.wave_id,
                        principalSchema: "mig",
                        principalTable: "wave",
                        principalColumn: "wave_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_wave_name",
                schema: "mig",
                table: "wave",
                columns: new[] { "legal_entity_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wave_term_batch",
                schema: "mig",
                table: "wave_term",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ux_wave_term_claim",
                schema: "mig",
                table: "wave_term",
                columns: new[] { "legal_entity_id", "object_type", "legacy_key" },
                unique: true,
                filter: "state IN ('PLANNED','OFFERED','CONVERTED')");

            migrationBuilder.CreateIndex(
                name: "ux_wave_term_wave_key",
                schema: "mig",
                table: "wave_term",
                columns: new[] { "wave_id", "legacy_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wave_term",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "wave",
                schema: "mig");

            migrationBuilder.DropColumn(
                name: "mapping_content_hash",
                schema: "mig",
                table: "batch");

            migrationBuilder.DropColumn(
                name: "mapping_spec_id",
                schema: "mig",
                table: "batch");
        }
    }
}
