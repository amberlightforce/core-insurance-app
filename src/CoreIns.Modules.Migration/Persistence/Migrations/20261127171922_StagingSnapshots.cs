using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Migration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StagingSnapshots : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staging_snapshot",
                schema: "mig",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    counts = table.Column<string>(type: "jsonb", nullable: false),
                    loaded_by = table.Column<string>(type: "text", nullable: false),
                    loaded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staging_snapshot", x => x.snapshot_id);
                    table.CheckConstraint("ck_staging_snapshot_status", "status IN ('ACTIVE','SUPERSEDED')");
                });

            migrationBuilder.CreateTable(
                name: "staging_record",
                schema: "mig",
                columns: table => new
                {
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    legacy_key = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    row_hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staging_record", x => x.record_id);
                    table.ForeignKey(
                        name: "fk_staging_record_snapshot",
                        column: x => x.snapshot_id,
                        principalSchema: "mig",
                        principalTable: "staging_snapshot",
                        principalColumn: "snapshot_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staging_record_key",
                schema: "mig",
                table: "staging_record",
                columns: new[] { "snapshot_id", "object_type", "legacy_key" });

            migrationBuilder.CreateIndex(
                name: "ux_staging_record_seq",
                schema: "mig",
                table: "staging_record",
                columns: new[] { "snapshot_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_staging_snapshot_version",
                schema: "mig",
                table: "staging_snapshot",
                columns: new[] { "legal_entity_id", "source", "snapshot", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staging_record",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "staging_snapshot",
                schema: "mig");
        }
    }
}
