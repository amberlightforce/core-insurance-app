using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRenewalRun : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "renewal_run",
                schema: "pol",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    dry_run = table.Column<bool>(type: "boolean", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: true),
                    window_days = table.Column<int>(type: "integer", nullable: false),
                    selected_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    cursor = table.Column<int>(type: "integer", nullable: false),
                    lease_until = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_renewal_run", x => x.run_id);
                    table.CheckConstraint("ck_renewal_run_status", "status IN ('RUNNING', 'PAUSED', 'COMPLETED', 'COMPLETED_WITH_ERRORS')");
                });

            migrationBuilder.CreateTable(
                name: "renewal_run_item",
                schema: "pol",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_renewal_run_item", x => x.item_id);
                    table.CheckConstraint("ck_renewal_run_item_outcome", "outcome IN ('PENDING', 'CREATED', 'WOULD_CREATE', 'SKIPPED_NON_RENEWED', 'SKIPPED_VULNERABLE', 'SKIPPED_EXISTING', 'FAILED')");
                    table.ForeignKey(
                        name: "fk_renewal_run_item_run",
                        column: x => x.run_id,
                        principalSchema: "pol",
                        principalTable: "renewal_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_renewal_run_entity",
                schema: "pol",
                table: "renewal_run",
                columns: new[] { "legal_entity_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_renewal_run_active",
                schema: "pol",
                table: "renewal_run",
                column: "legal_entity_id",
                unique: true,
                filter: "status IN ('RUNNING', 'PAUSED')");

            migrationBuilder.CreateIndex(
                name: "ix_renewal_run_item_outcome",
                schema: "pol",
                table: "renewal_run_item",
                columns: new[] { "run_id", "outcome", "seq" });

            migrationBuilder.CreateIndex(
                name: "ux_renewal_run_item_term",
                schema: "pol",
                table: "renewal_run_item",
                columns: new[] { "run_id", "term_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "renewal_run_item",
                schema: "pol");

            migrationBuilder.DropTable(
                name: "renewal_run",
                schema: "pol");
        }
    }
}
