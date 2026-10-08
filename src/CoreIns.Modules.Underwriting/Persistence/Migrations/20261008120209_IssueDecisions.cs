using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Underwriting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IssueDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_issue_open_key",
                schema: "uw",
                table: "issue");

            migrationBuilder.AddColumn<Guid>(
                name: "authority_check_id",
                schema: "uw",
                table: "issue",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "decided_at",
                schema: "uw",
                table: "issue",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decided_by",
                schema: "uw",
                table: "issue",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision",
                schema: "uw",
                table: "issue",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_fingerprint",
                schema: "uw",
                table: "issue",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_message",
                schema: "uw",
                table: "issue",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_reason",
                schema: "uw",
                table: "issue",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fingerprint",
                schema: "uw",
                table: "issue",
                type: "char(64)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_issue_status",
                schema: "uw",
                table: "issue",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_issue_open_key",
                schema: "uw",
                table: "issue",
                columns: new[] { "job_id", "issue_key" },
                unique: true,
                filter: "status IN ('Open', 'Approved', 'ApprovedWithConditions', 'Rejected')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_issue_decided",
                schema: "uw",
                table: "issue",
                sql: "status NOT IN ('Approved', 'ApprovedWithConditions', 'Rejected') OR (decision IS NOT NULL AND decided_by IS NOT NULL AND decided_at IS NOT NULL AND decision_reason IS NOT NULL AND decision_fingerprint IS NOT NULL AND authority_check_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_issue_decision",
                schema: "uw",
                table: "issue",
                sql: "decision IS NULL OR decision IN ('APPROVE', 'REJECT')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_issue_status",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropIndex(
                name: "ux_issue_open_key",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropCheckConstraint(
                name: "ck_issue_decided",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropCheckConstraint(
                name: "ck_issue_decision",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropColumn(
                name: "authority_check_id",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropColumn(
                name: "decided_at",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropColumn(
                name: "decided_by",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropColumn(
                name: "decision",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropColumn(
                name: "decision_fingerprint",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropColumn(
                name: "decision_message",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropColumn(
                name: "decision_reason",
                schema: "uw",
                table: "issue");

            migrationBuilder.DropColumn(
                name: "fingerprint",
                schema: "uw",
                table: "issue");

            migrationBuilder.CreateIndex(
                name: "ux_issue_open_key",
                schema: "uw",
                table: "issue",
                columns: new[] { "job_id", "issue_key" },
                unique: true,
                filter: "status = 'Open'");
        }
    }
}
