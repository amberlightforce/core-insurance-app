using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActivityCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activity",
                schema: "wrk",
                columns: table => new
                {
                    activity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    pattern_code = table.Column<string>(type: "text", nullable: false),
                    pattern_version = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    sla_policy = table.Column<string>(type: "text", nullable: false),
                    gate_blocking = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    primary_module = table.Column<string>(type: "text", nullable: false),
                    primary_type = table.Column<string>(type: "text", nullable: false),
                    primary_id = table.Column<string>(type: "text", nullable: false),
                    secondary_refs = table.Column<string>(type: "jsonb", nullable: false),
                    subject_party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    raising_module = table.Column<string>(type: "text", nullable: false),
                    cause_ref = table.Column<string>(type: "text", nullable: false),
                    assignee_type = table.Column<string>(type: "text", nullable: false),
                    assignee_id = table.Column<string>(type: "text", nullable: false),
                    queue_code = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    target_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    escalation_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    breached_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    closed_by = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    close_reason = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity", x => x.activity_id);
                    table.CheckConstraint("ck_activity_assignee_type", "assignee_type IN ('USER', 'GROUP', 'QUEUE')");
                    table.CheckConstraint("ck_activity_closed", "(state = 'OPEN') = (closed_at IS NULL)");
                    table.CheckConstraint("ck_activity_state", "state IN ('OPEN', 'COMPLETED', 'SKIPPED', 'CANCELLED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_activity_inbox",
                schema: "wrk",
                table: "activity",
                columns: new[] { "legal_entity_id", "state", "assignee_type", "assignee_id", "target_at" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_primary_object",
                schema: "wrk",
                table: "activity",
                columns: new[] { "legal_entity_id", "primary_module", "primary_type", "primary_id" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_sla_sweep",
                schema: "wrk",
                table: "activity",
                column: "target_at",
                filter: "state = 'OPEN' AND breached_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_activity_subject_party",
                schema: "wrk",
                table: "activity",
                column: "subject_party_id");

            migrationBuilder.CreateIndex(
                name: "ux_activity_number",
                schema: "wrk",
                table: "activity",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_activity_open_duplicate",
                schema: "wrk",
                table: "activity",
                columns: new[] { "legal_entity_id", "pattern_code", "primary_module", "primary_type", "primary_id", "cause_ref" },
                unique: true,
                filter: "state = 'OPEN'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity",
                schema: "wrk");
        }
    }
}
