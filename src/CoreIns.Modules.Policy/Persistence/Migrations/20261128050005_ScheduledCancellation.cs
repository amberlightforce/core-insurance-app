using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledCancellation : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_cancellation",
                schema: "pol",
                columns: table => new
                {
                    scheduled_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    effective_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    refund_method = table.Column<string>(type: "text", nullable: false),
                    request_ref = table.Column<string>(type: "text", nullable: true),
                    evidence_refs = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    executed_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduled_cancellation", x => x.scheduled_id);
                    table.CheckConstraint("ck_scheduled_cancellation_status", "status IN ('SCHEDULED', 'EXECUTED', 'RESCINDED', 'FAILED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_cancellation_due",
                schema: "pol",
                table: "scheduled_cancellation",
                columns: new[] { "status", "effective_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_cancellation_policy",
                schema: "pol",
                table: "scheduled_cancellation",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ux_scheduled_cancellation_open_term",
                schema: "pol",
                table: "scheduled_cancellation",
                column: "term_id",
                unique: true,
                filter: "status = 'SCHEDULED'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_cancellation",
                schema: "pol");
        }
    }
}
