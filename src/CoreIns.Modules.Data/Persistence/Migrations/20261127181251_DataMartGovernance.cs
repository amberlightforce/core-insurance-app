using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataMartGovernance : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mart_adjustment",
                schema: "rpt_mart",
                columns: table => new
                {
                    adjustment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    figure_key = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(20,4)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    evidence_ref = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mart_adjustment", x => x.adjustment_id);
                    table.ForeignKey(
                        name: "fk_mart_adjustment_run",
                        column: x => x.run_id,
                        principalSchema: "rpt_mart",
                        principalTable: "mart_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mart_signoff",
                schema: "rpt_mart",
                columns: table => new
                {
                    signoff_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_hash = table.Column<string>(type: "text", nullable: false),
                    adjustments_hash = table.Column<string>(type: "text", nullable: false),
                    bound_hash = table.Column<string>(type: "text", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: false),
                    signed_by = table.Column<string>(type: "text", nullable: false),
                    signed_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mart_signoff", x => x.signoff_id);
                    table.ForeignKey(
                        name: "fk_mart_signoff_run",
                        column: x => x.run_id,
                        principalSchema: "rpt_mart",
                        principalTable: "mart_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mart_adjustment_run",
                schema: "rpt_mart",
                table: "mart_adjustment",
                columns: new[] { "run_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mart_signoff_run",
                schema: "rpt_mart",
                table: "mart_signoff",
                columns: new[] { "run_id", "signed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mart_adjustment",
                schema: "rpt_mart");

            migrationBuilder.DropTable(
                name: "mart_signoff",
                schema: "rpt_mart");
        }
    }
}
