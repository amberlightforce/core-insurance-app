using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Underwriting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Contingencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contingency",
                schema: "uw",
                columns: table => new
                {
                    contingency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contingency_type = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    customer_text = table.Column<string>(type: "text", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: false),
                    blocking_point = table.Column<string>(type: "text", nullable: false),
                    consequence_code = table.Column<string>(type: "text", nullable: false),
                    consequence_issue_type = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    overdue_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    resolved_by = table.Column<string>(type: "text", nullable: true),
                    resolution_note = table.Column<string>(type: "text", nullable: true),
                    evidence_ref = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contingency", x => x.contingency_id);
                    table.CheckConstraint("ck_contingency_blocking_point", "blocking_point IN ('PRE_QUOTE', 'PRE_BIND', 'PRE_ISSUE', 'NON_BLOCKING')");
                    table.CheckConstraint("ck_contingency_consequence", "consequence_code IN ('NONE', 'CANCEL_QUOTE', 'ADD_ISSUE')");
                    table.CheckConstraint("ck_contingency_status", "status IN ('Open', 'Satisfied', 'Waived', 'ConsequenceApplied')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_contingency_due",
                schema: "uw",
                table: "contingency",
                columns: new[] { "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_contingency_job",
                schema: "uw",
                table: "contingency",
                columns: new[] { "legal_entity_id", "job_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_contingency_term",
                schema: "uw",
                table: "contingency",
                columns: new[] { "legal_entity_id", "term_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contingency",
                schema: "uw");
        }
    }
}
