using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubmissionTracker : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "submission",
                schema: "cmp",
                columns: table => new
                {
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    previous_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    submission_type = table.Column<string>(type: "text", nullable: false),
                    regulator = table.Column<string>(type: "text", nullable: false),
                    period_label = table.Column<string>(type: "text", nullable: false),
                    period_from = table.Column<DateOnly>(type: "date", nullable: true),
                    period_to = table.Column<DateOnly>(type: "date", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    due_legal_status = table.Column<string>(type: "text", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    participants = table.Column<string>(type: "jsonb", nullable: false),
                    approval_status = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: true),
                    prepared_by = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    filing_channel = table.Column<string>(type: "text", nullable: true),
                    filed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    filer = table.Column<string>(type: "text", nullable: true),
                    filing_reference = table.Column<string>(type: "text", nullable: true),
                    receipt_hash = table.Column<string>(type: "text", nullable: true),
                    response_outcome = table.Column<string>(type: "text", nullable: true),
                    response_reference = table.Column<string>(type: "text", nullable: true),
                    response_note = table.Column<string>(type: "text", nullable: true),
                    responded_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submission", x => x.submission_id);
                    table.CheckConstraint("ck_submission_approval_status", "approval_status IN ('NONE','PENDING','APPROVED')");
                    table.CheckConstraint("ck_submission_due_legal_status", "due_legal_status IN ('Verified','Unverified')");
                    table.CheckConstraint("ck_submission_status", "status IN ('PLANNED','IN_PREPARATION','READY','SUBMITTED','ACKNOWLEDGED','REJECTED','RESUBMITTED')");
                    table.CheckConstraint("ck_submission_version", "version >= 1 AND record_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "submission_history",
                schema: "cmp",
                columns: table => new
                {
                    history_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    text = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submission_history", x => x.history_id);
                    table.CheckConstraint("ck_submission_history_kind", "kind IN ('TRANSITION','COMMENT')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_submission_status",
                schema: "cmp",
                table: "submission",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_submission_number",
                schema: "cmp",
                table: "submission",
                columns: new[] { "legal_entity_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_submission_previous_version",
                schema: "cmp",
                table: "submission",
                column: "previous_version_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_submission_history_submission",
                schema: "cmp",
                table: "submission_history",
                columns: new[] { "submission_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "submission",
                schema: "cmp");

            migrationBuilder.DropTable(
                name: "submission_history",
                schema: "cmp");
        }
    }
}
