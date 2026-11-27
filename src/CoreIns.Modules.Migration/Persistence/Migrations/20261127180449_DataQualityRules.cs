using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Migration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataQualityRules : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dq_rule",
                schema: "mig",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_set_version = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dq_rule", x => x.rule_id);
                });

            migrationBuilder.CreateTable(
                name: "dq_run",
                schema: "mig",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_set_version = table.Column<int>(type: "integer", nullable: false),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    warning_count = table.Column<int>(type: "integer", nullable: false),
                    findings_by_rule = table.Column<string>(type: "jsonb", nullable: false),
                    profile = table.Column<string>(type: "jsonb", nullable: false),
                    run_by = table.Column<string>(type: "text", nullable: false),
                    run_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dq_run", x => x.run_id);
                    table.ForeignKey(
                        name: "fk_dq_run_snapshot",
                        column: x => x.snapshot_id,
                        principalSchema: "mig",
                        principalTable: "staging_snapshot",
                        principalColumn: "snapshot_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dq_finding",
                schema: "mig",
                columns: table => new
                {
                    finding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    legacy_key = table.Column<string>(type: "text", nullable: false),
                    rule_code = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    field = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dq_finding", x => x.finding_id);
                    table.CheckConstraint("ck_dq_finding_severity", "severity IN ('ERROR','WARNING')");
                    table.ForeignKey(
                        name: "fk_dq_finding_run",
                        column: x => x.run_id,
                        principalSchema: "mig",
                        principalTable: "dq_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dq_waiver",
                schema: "mig",
                columns: table => new
                {
                    waiver_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    legacy_key = table.Column<string>(type: "text", nullable: false),
                    rule_code = table.Column<string>(type: "text", nullable: false),
                    finding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: false),
                    waived_by = table.Column<string>(type: "text", nullable: false),
                    waived_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dq_waiver", x => x.waiver_id);
                    table.ForeignKey(
                        name: "fk_dq_waiver_finding",
                        column: x => x.finding_id,
                        principalSchema: "mig",
                        principalTable: "dq_finding",
                        principalColumn: "finding_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dq_finding_rule",
                schema: "mig",
                table: "dq_finding",
                columns: new[] { "run_id", "rule_code" });

            migrationBuilder.CreateIndex(
                name: "ux_dq_finding_run_seq",
                schema: "mig",
                table: "dq_finding",
                columns: new[] { "run_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dq_rule_version_code",
                schema: "mig",
                table: "dq_rule",
                columns: new[] { "rule_set_version", "code", "object_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dq_run_snapshot",
                schema: "mig",
                table: "dq_run",
                columns: new[] { "snapshot_id", "run_at" });

            migrationBuilder.CreateIndex(
                name: "IX_dq_waiver_finding_id",
                schema: "mig",
                table: "dq_waiver",
                column: "finding_id");

            migrationBuilder.CreateIndex(
                name: "ix_dq_waiver_key",
                schema: "mig",
                table: "dq_waiver",
                columns: new[] { "legal_entity_id", "source", "rule_code", "object_type", "legacy_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dq_rule",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "dq_waiver",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "dq_finding",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "dq_run",
                schema: "mig");
        }
    }
}
