using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Underwriting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RuleSetRelease : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "approval_request_id",
                schema: "uw",
                table: "rule_set_version",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "decided_at",
                schema: "uw",
                table: "rule_set_version",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decided_by",
                schema: "uw",
                table: "rule_set_version",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_reason",
                schema: "uw",
                table: "rule_set_version",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "record_version",
                schema: "uw",
                table: "rule_set_version",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "submitted_at",
                schema: "uw",
                table: "rule_set_version",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "submitted_by",
                schema: "uw",
                table: "rule_set_version",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "issue_type",
                schema: "uw",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    name_el = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    default_lane = table.Column<string>(type: "text", nullable: false),
                    blocking_point = table.Column<string>(type: "text", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issue_type", x => x.code);
                    table.CheckConstraint("ck_issue_type_blocking", "blocking_point IN ('PRE_QUOTE', 'PRE_BIND', 'PRE_ISSUE', 'NON_BLOCKING')");
                    table.CheckConstraint("ck_issue_type_lane", "default_lane IN ('STRAIGHT_THROUGH', 'ASSISTED', 'EXPERT')");
                    table.CheckConstraint("ck_issue_type_severity", "severity IN ('REFER', 'DECLINE')");
                });

            // The issue types of the built-in illustrative rule sets (ILLUSTRATIVE TEST DATA, like the rules).
            migrationBuilder.Sql(
                """
                INSERT INTO uw.issue_type (code, name_el, name_en, severity, default_lane, blocking_point, active, record_version, updated_at, updated_by) VALUES
                 ('DRIVER_UNDERAGE', 'Ανήλικος οδηγός', 'Underage driver', 'DECLINE', 'EXPERT', 'PRE_QUOTE', true, 1, now(), 'system'),
                 ('CLAIMS_HISTORY', 'Ιστορικό ζημιών', 'Claims history', 'DECLINE', 'EXPERT', 'PRE_QUOTE', true, 1, now(), 'system'),
                 ('USAGE_NOT_ELIGIBLE', 'Μη επιλέξιμη χρήση', 'Usage not eligible', 'DECLINE', 'EXPERT', 'PRE_QUOTE', true, 1, now(), 'system'),
                 ('DRIVER_AGE_REFERRAL', 'Παραπομπή ηλικίας οδηγού', 'Driver age referral', 'REFER', 'ASSISTED', 'PRE_BIND', true, 1, now(), 'system'),
                 ('VEHICLE_AGE_REFERRAL', 'Παραπομπή ηλικίας οχήματος', 'Vehicle age referral', 'REFER', 'ASSISTED', 'PRE_BIND', true, 1, now(), 'system'),
                 ('VEHICLE_VALUE_REFERRAL', 'Παραπομπή αξίας οχήματος', 'Vehicle value referral', 'REFER', 'ASSISTED', 'PRE_BIND', true, 1, now(), 'system')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "issue_type",
                schema: "uw");

            migrationBuilder.DropColumn(
                name: "approval_request_id",
                schema: "uw",
                table: "rule_set_version");

            migrationBuilder.DropColumn(
                name: "decided_at",
                schema: "uw",
                table: "rule_set_version");

            migrationBuilder.DropColumn(
                name: "decided_by",
                schema: "uw",
                table: "rule_set_version");

            migrationBuilder.DropColumn(
                name: "decision_reason",
                schema: "uw",
                table: "rule_set_version");

            migrationBuilder.DropColumn(
                name: "record_version",
                schema: "uw",
                table: "rule_set_version");

            migrationBuilder.DropColumn(
                name: "submitted_at",
                schema: "uw",
                table: "rule_set_version");

            migrationBuilder.DropColumn(
                name: "submitted_by",
                schema: "uw",
                table: "rule_set_version");
        }
    }
}
