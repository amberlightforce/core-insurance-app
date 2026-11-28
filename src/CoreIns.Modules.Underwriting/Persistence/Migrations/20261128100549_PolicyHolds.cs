using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Underwriting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PolicyHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "policy_hold",
                schema: "uw",
                columns: table => new
                {
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hold_code = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    job_types = table.Column<string[]>(type: "text[]", nullable: false),
                    copied_from = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    activated_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    activated_by = table.Column<string>(type: "text", nullable: true),
                    released_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    released_by = table.Column<string>(type: "text", nullable: true),
                    release_reason = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_hold", x => x.hold_id);
                    table.CheckConstraint("ck_policy_hold_status", "status IN ('Draft', 'Active', 'Released')");
                    table.CheckConstraint("ck_policy_hold_window", "effective_to IS NULL OR effective_to >= effective_from");
                });

            migrationBuilder.CreateTable(
                name: "hold_scope",
                schema: "uw",
                columns: table => new
                {
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dimension = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    retired_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hold_scope", x => x.scope_id);
                    table.CheckConstraint("ck_hold_scope_dimension", "dimension IN ('POSTCODE', 'HAZARD_ZONE', 'PRODUCT', 'LINE_OF_BUSINESS', 'COVERAGE')");
                    table.ForeignKey(
                        name: "fk_hold_scope_hold",
                        column: x => x.hold_id,
                        principalSchema: "uw",
                        principalTable: "policy_hold",
                        principalColumn: "hold_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_hold_scope_value",
                schema: "uw",
                table: "hold_scope",
                columns: new[] { "hold_id", "dimension", "value" },
                unique: true,
                filter: "retired_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_policy_hold_status",
                schema: "uw",
                table: "policy_hold",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_policy_hold_code",
                schema: "uw",
                table: "policy_hold",
                columns: new[] { "legal_entity_id", "hold_code" },
                unique: true);
            // The issue type a hold raises on a job (BO-UW-09).
            migrationBuilder.Sql(
                """
                INSERT INTO uw.issue_type (code, name_el, name_en, severity, default_lane, blocking_point, active, record_version, updated_at, updated_by) VALUES
                 ('UW_POLICY_HOLD', 'Αναστολή παραγωγής', 'Policy hold', 'REFER', 'EXPERT', 'PRE_BIND', true, 1, now(), 'system')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hold_scope",
                schema: "uw");

            migrationBuilder.DropTable(
                name: "policy_hold",
                schema: "uw");
        }
    }
}
