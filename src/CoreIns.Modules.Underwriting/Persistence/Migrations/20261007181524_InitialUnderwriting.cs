using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Underwriting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialUnderwriting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "uw");

            migrationBuilder.CreateTable(
                name: "rule_set_version",
                schema: "uw",
                columns: table => new
                {
                    rule_set_code = table.Column<string>(type: "text", nullable: false),
                    version_no = table.Column<string>(type: "text", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    data_status = table.Column<string>(type: "text", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_set_version", x => new { x.rule_set_code, x.version_no });
                    table.CheckConstraint("ck_rule_set_version_data", "data_status IN ('ILLUSTRATIVE_TEST_DATA', 'APPROVED')");
                    table.CheckConstraint("ck_rule_set_version_status", "status IN ('Draft', 'Submitted', 'Approved', 'Active', 'Superseded', 'Retired')");
                });

            migrationBuilder.CreateTable(
                name: "evaluation",
                schema: "uw",
                columns: table => new
                {
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checkpoint = table.Column<string>(type: "text", nullable: false),
                    rule_set_code = table.Column<string>(type: "text", nullable: false),
                    rule_set_version = table.Column<string>(type: "text", nullable: false),
                    rule_set_hash = table.Column<string>(type: "char(64)", nullable: false),
                    snapshot_ref = table.Column<string>(type: "text", nullable: false),
                    snapshot_hash = table.Column<string>(type: "char(64)", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    lane = table.Column<string>(type: "text", nullable: false),
                    trace = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evaluation", x => x.evaluation_id);
                    table.CheckConstraint("ck_evaluation_checkpoint", "checkpoint IN ('PRE_QUOTE', 'PRE_BIND', 'PRE_ISSUE', 'RENEWAL')");
                    table.CheckConstraint("ck_evaluation_outcome", "outcome IN ('ACCEPT', 'REFER', 'DECLINE')");
                    table.ForeignKey(
                        name: "fk_evaluation_rule_set",
                        columns: x => new { x.rule_set_code, x.rule_set_version },
                        principalSchema: "uw",
                        principalTable: "rule_set_version",
                        principalColumns: new[] { "rule_set_code", "version_no" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "issue",
                schema: "uw",
                columns: table => new
                {
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_type = table.Column<string>(type: "text", nullable: false),
                    issue_key = table.Column<string>(type: "text", nullable: false),
                    blocking_point = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    lane = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    message_en = table.Column<string>(type: "text", nullable: false),
                    message_el = table.Column<string>(type: "text", nullable: false),
                    raised_evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_evaluation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    close_reason = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issue", x => x.issue_id);
                    table.CheckConstraint("ck_issue_blocking_point", "blocking_point IN ('PRE_QUOTE', 'PRE_BIND', 'PRE_ISSUE', 'NON_BLOCKING')");
                    table.CheckConstraint("ck_issue_severity", "severity IN ('REFER', 'DECLINE', 'WARN')");
                    table.CheckConstraint("ck_issue_status", "status IN ('Open', 'Approved', 'ApprovedWithConditions', 'Rejected', 'Invalidated', 'Closed')");
                    table.ForeignKey(
                        name: "fk_issue_raised_evaluation",
                        column: x => x.raised_evaluation_id,
                        principalSchema: "uw",
                        principalTable: "evaluation",
                        principalColumn: "evaluation_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_job",
                schema: "uw",
                table: "evaluation",
                columns: new[] { "job_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_evaluation_rule_set_code_rule_set_version",
                schema: "uw",
                table: "evaluation",
                columns: new[] { "rule_set_code", "rule_set_version" });

            migrationBuilder.CreateIndex(
                name: "ix_issue_job",
                schema: "uw",
                table: "issue",
                columns: new[] { "job_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_issue_raised_evaluation_id",
                schema: "uw",
                table: "issue",
                column: "raised_evaluation_id");

            migrationBuilder.CreateIndex(
                name: "ux_issue_open_key",
                schema: "uw",
                table: "issue",
                columns: new[] { "job_id", "issue_key" },
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_rule_set_version_product",
                schema: "uw",
                table: "rule_set_version",
                columns: new[] { "product_code", "status", "effective_from" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "issue",
                schema: "uw");

            migrationBuilder.DropTable(
                name: "evaluation",
                schema: "uw");

            migrationBuilder.DropTable(
                name: "rule_set_version",
                schema: "uw");
        }
    }
}
