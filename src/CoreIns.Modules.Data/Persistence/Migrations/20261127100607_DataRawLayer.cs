using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataRawLayer : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rpt_raw");

            migrationBuilder.CreateTable(
                name: "data_contract",
                schema: "dat",
                columns: table => new
                {
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    major = table.Column<int>(type: "integer", nullable: false),
                    minor = table.Column<int>(type: "integer", nullable: false),
                    producer = table.Column<string>(type: "text", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    compatibility = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_contract", x => x.contract_id);
                });

            migrationBuilder.CreateTable(
                name: "event_ledger",
                schema: "rpt_raw",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registry_name = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    producer = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<string>(type: "text", nullable: false),
                    aggregate_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<string>(type: "text", nullable: false),
                    aggregate_sequence = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_ledger", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "quarantine",
                schema: "rpt_raw",
                columns: table => new
                {
                    quarantine_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registry_name = table.Column<string>(type: "text", nullable: false),
                    reasons = table.Column<string>(type: "jsonb", nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    released_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quarantine", x => x.quarantine_id);
                    table.CheckConstraint("ck_quarantine_status", "status IN ('QUARANTINED','RELEASED')");
                });

            migrationBuilder.CreateIndex(
                name: "ux_data_contract_version",
                schema: "dat",
                table: "data_contract",
                columns: new[] { "name", "major", "minor" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_event_ledger_registry_occurred",
                schema: "rpt_raw",
                table: "event_ledger",
                columns: new[] { "registry_name", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_quarantine_status",
                schema: "rpt_raw",
                table: "quarantine",
                columns: new[] { "status", "registry_name" });

            migrationBuilder.CreateIndex(
                name: "ux_quarantine_event",
                schema: "rpt_raw",
                table: "quarantine",
                column: "event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_contract",
                schema: "dat");

            migrationBuilder.DropTable(
                name: "event_ledger",
                schema: "rpt_raw");

            migrationBuilder.DropTable(
                name: "quarantine",
                schema: "rpt_raw");
        }
    }
}
