using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClockEngine : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clock",
                schema: "cmp",
                columns: table => new
                {
                    clock_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    clock_code = table.Column<string>(type: "text", nullable: false),
                    subject_module = table.Column<string>(type: "text", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    variant = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    deadline = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    warn_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    paused_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    met_late = table.Column<bool>(type: "boolean", nullable: false),
                    value_version = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clock", x => x.clock_id);
                    table.CheckConstraint("ck_clock_kind", "kind IN ('WAITING_PERIOD','DEADLINE')");
                    table.CheckConstraint("ck_clock_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_clock_status", "status IN ('RUNNING','WARNED','PAUSED','MET','BREACHED','ELAPSED','CANCELLED')");
                    table.CheckConstraint("ck_clock_value_version", "value_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "clock_event",
                schema: "cmp",
                columns: table => new
                {
                    clock_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clock_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clock_event", x => x.clock_event_id);
                    table.ForeignKey(
                        name: "fk_clock_event_clock",
                        column: x => x.clock_id,
                        principalSchema: "cmp",
                        principalTable: "clock",
                        principalColumn: "clock_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clock_status_deadline",
                schema: "cmp",
                table: "clock",
                columns: new[] { "status", "deadline" });

            migrationBuilder.CreateIndex(
                name: "ix_clock_subject",
                schema: "cmp",
                table: "clock",
                columns: new[] { "subject_module", "subject_type", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ux_clock_instance",
                schema: "cmp",
                table: "clock",
                columns: new[] { "legal_entity_id", "clock_code", "subject_module", "subject_type", "subject_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clock_event_clock",
                schema: "cmp",
                table: "clock_event",
                columns: new[] { "clock_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clock_event",
                schema: "cmp");

            migrationBuilder.DropTable(
                name: "clock",
                schema: "cmp");
        }
    }
}
