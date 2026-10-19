using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DsarCases : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "disclosure_log",
                schema: "cmp",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dsar_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    legal_basis = table.Column<string>(type: "text", nullable: false),
                    categories = table.Column<string>(type: "jsonb", nullable: false),
                    recorded_by = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disclosure_log", x => x.entry_id);
                });

            migrationBuilder.CreateTable(
                name: "dsar",
                schema: "cmp",
                columns: table => new
                {
                    dsar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    subject_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    deadline = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    extended = table.Column<bool>(type: "boolean", nullable: false),
                    identity_verified = table.Column<bool>(type: "boolean", nullable: false),
                    identity_method = table.Column<string>(type: "text", nullable: true),
                    verified_by = table.Column<string>(type: "text", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    approval_action = table.Column<string>(type: "text", nullable: true),
                    approval_reason = table.Column<string>(type: "text", nullable: true),
                    legal_hold = table.Column<bool>(type: "boolean", nullable: false),
                    refusal_reason = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    package = table.Column<string>(type: "jsonb", nullable: true),
                    action_result = table.Column<string>(type: "jsonb", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dsar", x => x.dsar_id);
                    table.CheckConstraint("ck_dsar_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_dsar_request_type", "request_type IN ('ACCESS','PORTABILITY','ERASURE','RESTRICTION','OBJECTION')");
                    table.CheckConstraint("ck_dsar_status", "status IN ('RECEIVED','IDENTITY_VERIFIED','IN_REVIEW','COMPLETED','REFUSED')");
                });

            migrationBuilder.CreateTable(
                name: "dsar_task",
                schema: "cmp",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dsar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "text", nullable: false),
                    export_only = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    phase = table.Column<string>(type: "text", nullable: false),
                    outcomes = table.Column<string>(type: "jsonb", nullable: false),
                    export_payload = table.Column<string>(type: "jsonb", nullable: true),
                    redact_third_party = table.Column<bool>(type: "boolean", nullable: false),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dsar_task", x => x.task_id);
                    table.CheckConstraint("ck_dsar_task_phase", "phase IN ('EXPORT','RESTRICT','ERASE')");
                    table.CheckConstraint("ck_dsar_task_status", "status IN ('PENDING','DONE','FAILED','NOT_BUILT')");
                    table.ForeignKey(
                        name: "fk_dsar_task_dsar",
                        column: x => x.dsar_id,
                        principalSchema: "cmp",
                        principalTable: "dsar",
                        principalColumn: "dsar_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_disclosure_log_subject",
                schema: "cmp",
                table: "disclosure_log",
                columns: new[] { "legal_entity_id", "subject_party_id" });

            migrationBuilder.CreateIndex(
                name: "ix_dsar_subject",
                schema: "cmp",
                table: "dsar",
                columns: new[] { "legal_entity_id", "subject_party_id" });

            migrationBuilder.CreateIndex(
                name: "ux_dsar_task_module",
                schema: "cmp",
                table: "dsar_task",
                columns: new[] { "dsar_id", "module" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "disclosure_log",
                schema: "cmp");

            migrationBuilder.DropTable(
                name: "dsar_task",
                schema: "cmp");

            migrationBuilder.DropTable(
                name: "dsar",
                schema: "cmp");
        }
    }
}
