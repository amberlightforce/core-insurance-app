using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Migration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CutoverPlans : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cutover_plan",
                schema: "mig",
                columns: table => new
                {
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    rollback_previewed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    rolled_back_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cutover_plan", x => x.plan_id);
                    table.CheckConstraint("ck_cutover_plan_kind", "kind IN ('REHEARSAL','CUTOVER')");
                    table.CheckConstraint("ck_cutover_plan_status", "status IN ('PLANNED','IN_PROGRESS','COMPLETED','ROLLED_BACK','ROLLBACK_BLOCKED')");
                });

            migrationBuilder.CreateTable(
                name: "cutover_step",
                schema: "mig",
                columns: table => new
                {
                    step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    result = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cutover_step", x => x.step_id);
                    table.CheckConstraint("ck_cutover_step_status", "status IN ('PENDING','IN_PROGRESS','DONE','ROLLED_BACK')");
                    table.ForeignKey(
                        name: "fk_cutover_step_plan",
                        column: x => x.plan_id,
                        principalSchema: "mig",
                        principalTable: "cutover_plan",
                        principalColumn: "plan_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_cutover_plan_name",
                schema: "mig",
                table: "cutover_plan",
                columns: new[] { "legal_entity_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_cutover_step_seq",
                schema: "mig",
                table: "cutover_step",
                columns: new[] { "plan_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cutover_step",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "cutover_plan",
                schema: "mig");
        }
    }
}
