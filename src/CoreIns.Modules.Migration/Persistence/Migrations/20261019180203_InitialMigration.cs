using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Migration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMigration : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mig");

            migrationBuilder.CreateTable(
                name: "batch",
                schema: "mig",
                columns: table => new
                {
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    paused_from = table.Column<string>(type: "text", nullable: true),
                    baseline_hash = table.Column<string>(type: "text", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    dry_run_hash = table.Column<string>(type: "text", nullable: true),
                    dry_run_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    started_by = table.Column<string>(type: "text", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_batch", x => x.batch_id);
                });

            migrationBuilder.CreateTable(
                name: "batch_item",
                schema: "mig",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    module = table.Column<string>(type: "text", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    legacy_id = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    core_id = table.Column<Guid>(type: "uuid", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_batch_item", x => x.item_id);
                });

            migrationBuilder.CreateTable(
                name: "routing",
                schema: "mig",
                columns: table => new
                {
                    route_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    master_state = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_routing", x => x.route_id);
                    table.CheckConstraint("ck_routing_period", "valid_to IS NULL OR valid_to > valid_from");
                    table.CheckConstraint("ck_routing_state", "master_state IN ('NONE','TRANSITIONING','CORE')");
                });

            migrationBuilder.CreateTable(
                name: "xref",
                schema: "mig",
                columns: table => new
                {
                    xref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    legacy_key = table.Column<string>(type: "text", nullable: false),
                    core_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_xref", x => x.xref_id);
                    table.CheckConstraint("ck_xref_status", "status IN ('ACTIVE','REVERSED')");
                });

            migrationBuilder.CreateIndex(
                name: "ux_batch_item_legacy",
                schema: "mig",
                table: "batch_item",
                columns: new[] { "batch_id", "module", "object_type", "legacy_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_batch_item_seq",
                schema: "mig",
                table: "batch_item",
                columns: new[] { "batch_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_routing_from",
                schema: "mig",
                table: "routing",
                columns: new[] { "legal_entity_id", "object_type", "key", "valid_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_xref_core",
                schema: "mig",
                table: "xref",
                columns: new[] { "legal_entity_id", "object_type", "core_id" });

            migrationBuilder.CreateIndex(
                name: "ux_xref_legacy",
                schema: "mig",
                table: "xref",
                columns: new[] { "legal_entity_id", "source", "object_type", "legacy_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "batch",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "batch_item",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "routing",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "xref",
                schema: "mig");
        }
    }
}
