using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Migration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ControlTotalsAndReconResults : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "control_total",
                schema: "mig",
                columns: table => new
                {
                    control_total_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    module = table.Column<string>(type: "text", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    loaded_by = table.Column<string>(type: "text", nullable: false),
                    loaded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_control_total", x => x.control_total_id);
                });

            migrationBuilder.CreateTable(
                name: "recon_result",
                schema: "mig",
                columns: table => new
                {
                    result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    module = table.Column<string>(type: "text", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    legacy_count = table.Column<long>(type: "bigint", nullable: false),
                    core_count = table.Column<long>(type: "bigint", nullable: false),
                    legacy_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    core_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    run_by = table.Column<string>(type: "text", nullable: false),
                    run_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recon_result", x => x.result_id);
                    table.CheckConstraint("ck_recon_result_status", "status IN ('MATCH','VARIANCE','MISSING_CORE','MISSING_LEGACY')");
                });

            migrationBuilder.CreateIndex(
                name: "ux_control_total_key",
                schema: "mig",
                table: "control_total",
                columns: new[] { "legal_entity_id", "scope", "module", "object_type", "key", "currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recon_result_run",
                schema: "mig",
                table: "recon_result",
                columns: new[] { "legal_entity_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recon_result_scope",
                schema: "mig",
                table: "recon_result",
                columns: new[] { "legal_entity_id", "scope", "run_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "control_total",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "recon_result",
                schema: "mig");
        }
    }
}
