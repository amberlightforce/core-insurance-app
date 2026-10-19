using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiSystemRegister : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_system",
                schema: "cmp",
                columns: table => new
                {
                    feature_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ai_act_class = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    submitted_by = table.Column<string>(type: "text", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_system", x => x.feature_id);
                    table.CheckConstraint("ck_ai_system_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_ai_system_status", "status IN ('DRAFT','IN_REVIEW','APPROVED','SUSPENDED','RETIRED')");
                });

            // Pilot features seeded as DRAFT (SL6-CMP-AIREG): nothing can be enabled until the register approves it (D-SL6-08).
            // Ids and names follow the module PRDs; the AI Act class is illustrative until Compliance classifies each feature.
            migrationBuilder.Sql(
                """
                INSERT INTO cmp.ai_system (feature_id, name, ai_act_class, status, created_at, updated_at, record_version) VALUES
                ('AI-CLM-01', 'FNOL assistant', 'LIMITED', 'DRAFT', now(), now(), 1),
                ('AI-CLM-04', 'Claim triage (bias-tested)', 'HIGH_RISK_CONTROLS', 'DRAFT', now(), now(), 1),
                ('AI-BIL-01', 'Suspense match assistant', 'MINIMAL', 'DRAFT', now(), now(), 1),
                ('AI-PTY-01', 'Duplicate-match explainer', 'MINIMAL', 'DRAFT', now(), now(), 1),
                ('AI-CHN-02', 'MCP facade', 'MINIMAL', 'DRAFT', now(), now(), 1),
                ('AI-UW-01', 'Referral and submission risk brief', 'HIGH_RISK_CONTROLS', 'DRAFT', now(), now(), 1)
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_system",
                schema: "cmp");
        }
    }
}
