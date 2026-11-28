using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assignee_profile",
                schema: "wrk",
                columns: table => new
                {
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    roles = table.Column<string>(type: "text", nullable: false),
                    skills = table.Column<string>(type: "text", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignee_profile", x => new { x.legal_entity_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "assignment_rule",
                schema: "wrk",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    queue_code = table.Column<string>(type: "text", nullable: false),
                    pattern_code = table.Column<string>(type: "text", nullable: true),
                    required_role = table.Column<string>(type: "text", nullable: true),
                    required_skill = table.Column<string>(type: "text", nullable: true),
                    strategy = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignment_rule", x => x.rule_id);
                    table.CheckConstraint("ck_assignment_rule_strategy", "strategy IN ('ROUND_ROBIN_OPEN_COUNT')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_assignment_rule_queue",
                schema: "wrk",
                table: "assignment_rule",
                columns: new[] { "legal_entity_id", "queue_code", "priority" });

            migrationBuilder.CreateIndex(
                name: "ux_assignment_rule_code",
                schema: "wrk",
                table: "assignment_rule",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assignee_profile",
                schema: "wrk");

            migrationBuilder.DropTable(
                name: "assignment_rule",
                schema: "wrk");
        }
    }
}
