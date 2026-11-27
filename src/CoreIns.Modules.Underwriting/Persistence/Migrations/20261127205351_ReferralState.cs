using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Underwriting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReferralState : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "referral_state",
                schema: "uw",
                columns: table => new
                {
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lifecycle = table.Column<string>(type: "text", nullable: false),
                    awaiting_info = table.Column<bool>(type: "boolean", nullable: false),
                    info_comment = table.Column<string>(type: "text", nullable: true),
                    sla_paused_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    sla_due = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    priority = table.Column<string>(type: "text", nullable: false),
                    required_role = table.Column<string>(type: "text", nullable: true),
                    referred_by = table.Column<string>(type: "text", nullable: true),
                    request_comment = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    lock_owner = table.Column<string>(type: "text", nullable: true),
                    lock_acquired_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    lock_expires_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_referral_state", x => new { x.legal_entity_id, x.job_id });
                    table.CheckConstraint("ck_referral_state_lifecycle", "lifecycle IN ('Active', 'Withdrawn')");
                    table.CheckConstraint("ck_referral_state_lock", "(lock_owner IS NULL) = (lock_expires_at IS NULL)");
                    table.CheckConstraint("ck_referral_state_priority", "priority IN ('NORMAL', 'HIGH')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_referral_state_queue",
                schema: "uw",
                table: "referral_state",
                columns: new[] { "legal_entity_id", "lifecycle", "sla_due" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "referral_state",
                schema: "uw");
        }
    }
}
