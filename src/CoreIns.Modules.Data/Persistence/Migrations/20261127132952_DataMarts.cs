using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataMarts : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rpt_mart");

            migrationBuilder.CreateTable(
                name: "mart_run",
                schema: "rpt_mart",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    mart_code = table.Column<string>(type: "text", nullable: false),
                    period = table.Column<string>(type: "text", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    valid_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    known_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    definition_version = table.Column<string>(type: "text", nullable: false),
                    legal_status = table.Column<string>(type: "text", nullable: false),
                    snapshot_hash = table.Column<string>(type: "text", nullable: false),
                    snapshot_key = table.Column<string>(type: "text", nullable: false),
                    figure_count = table.Column<int>(type: "integer", nullable: false),
                    source_row_count = table.Column<long>(type: "bigint", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mart_run", x => x.run_id);
                });

            migrationBuilder.CreateTable(
                name: "mart_figure",
                schema: "rpt_mart",
                columns: table => new
                {
                    figure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    figure_key = table.Column<string>(type: "text", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    period_month = table.Column<string>(type: "text", nullable: false),
                    dimension = table.Column<string>(type: "text", nullable: false),
                    metric = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<decimal>(type: "numeric(20,4)", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    source_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mart_figure", x => x.figure_id);
                    table.ForeignKey(
                        name: "fk_mart_figure_run",
                        column: x => x.run_id,
                        principalSchema: "rpt_mart",
                        principalTable: "mart_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_mart_figure_run_key",
                schema: "rpt_mart",
                table: "mart_figure",
                columns: new[] { "run_id", "figure_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mart_run_entity_mart",
                schema: "rpt_mart",
                table: "mart_run",
                columns: new[] { "legal_entity", "mart_code", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mart_figure",
                schema: "rpt_mart");

            migrationBuilder.DropTable(
                name: "mart_run",
                schema: "rpt_mart");
        }
    }
}
