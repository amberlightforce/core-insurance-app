using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ComplaintCases : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "complaint",
                schema: "cmp",
                columns: table => new
                {
                    complaint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: true),
                    complainant_party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    complainant_type = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: true),
                    cause = table.Column<string>(type: "text", nullable: true),
                    product_line = table.Column<string>(type: "text", nullable: true),
                    vulnerable = table.Column<bool>(type: "boolean", nullable: false),
                    links = table.Column<string>(type: "jsonb", nullable: false),
                    actions = table.Column<string>(type: "jsonb", nullable: false),
                    acknowledged_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    acknowledgement_note = table.Column<string>(type: "text", nullable: true),
                    reply_status = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    reply_text = table.Column<string>(type: "text", nullable: true),
                    reply_author = table.Column<string>(type: "text", nullable: true),
                    reply_approved_by = table.Column<string>(type: "text", nullable: true),
                    redress_amount = table.Column<string>(type: "text", nullable: true),
                    root_cause = table.Column<string>(type: "text", nullable: true),
                    adr_body = table.Column<string>(type: "text", nullable: true),
                    adr_reference = table.Column<string>(type: "text", nullable: true),
                    answered_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    closed_reason = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    reply_clock_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rules_missing = table.Column<bool>(type: "boolean", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complaint", x => x.complaint_id);
                    table.CheckConstraint("ck_complaint_outcome", "outcome IS NULL OR outcome IN ('UPHELD','PARTIALLY_UPHELD','REJECTED')");
                    table.CheckConstraint("ck_complaint_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_complaint_reply_status", "reply_status IN ('NONE','PENDING_APPROVAL','RETURNED','APPROVED')");
                    table.CheckConstraint("ck_complaint_status", "status IN ('RECEIVED','ACKNOWLEDGED','UNDER_INVESTIGATION','ANSWERED','ESCALATED','CLOSED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_complaint_status",
                schema: "cmp",
                table: "complaint",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_complaint_number",
                schema: "cmp",
                table: "complaint",
                columns: new[] { "legal_entity_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "complaint",
                schema: "cmp");
        }
    }
}
