using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Reconciliation : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recon_run",
                schema: "fin",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_code = table.Column<string>(type: "text", nullable: false),
                    period_code = table.Column<string>(type: "text", nullable: false),
                    run_no = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    ran_by = table.Column<string>(type: "text", nullable: false),
                    open_breaks = table.Column<int>(type: "integer", nullable: false),
                    ran_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    totals = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recon_run", x => x.run_id);
                    table.CheckConstraint("ck_recon_run_period", "period_code ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                    table.CheckConstraint("ck_recon_run_state", "state IN ('CLEAN','BREAKS')");
                });

            migrationBuilder.CreateTable(
                name: "recon_source_total",
                schema: "fin",
                columns: table => new
                {
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    as_of = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    period_code = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,2)", nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    received_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recon_source_total", x => new { x.legal_entity_id, x.source, x.as_of, x.currency });
                    table.CheckConstraint("ck_recon_source_total_count", "item_count IS NULL OR item_count >= 0");
                    table.CheckConstraint("ck_recon_source_total_currency", "currency ~ '^[A-Z]{3}$'");
                });

            migrationBuilder.CreateTable(
                name: "recon_break",
                schema: "fin",
                columns: table => new
                {
                    break_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_code = table.Column<string>(type: "text", nullable: false),
                    period_code = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    source_amount = table.Column<decimal>(type: "numeric(19,2)", nullable: false),
                    ledger_amount = table.Column<decimal>(type: "numeric(19,2)", nullable: false),
                    source_count = table.Column<int>(type: "integer", nullable: true),
                    ledger_count = table.Column<int>(type: "integer", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_run_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recon_break", x => x.break_id);
                    table.CheckConstraint("ck_recon_break_kind", "kind IN ('AMOUNT','COUNT','MISSING_SOURCE','MISSING_LEDGER')");
                    table.CheckConstraint("ck_recon_break_state", "state IN ('OPEN','ACCEPTED','RESOLVED')");
                    table.ForeignKey(
                        name: "fk_recon_break_last_run",
                        column: x => x.last_run_id,
                        principalSchema: "fin",
                        principalTable: "recon_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recon_break_run",
                        column: x => x.run_id,
                        principalSchema: "fin",
                        principalTable: "recon_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recon_break_last_run_id",
                schema: "fin",
                table: "recon_break",
                column: "last_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_recon_break_period",
                schema: "fin",
                table: "recon_break",
                columns: new[] { "legal_entity_id", "period_code", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_recon_break_run_id",
                schema: "fin",
                table: "recon_break",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ux_recon_break_active",
                schema: "fin",
                table: "recon_break",
                columns: new[] { "legal_entity_id", "definition_code", "period_code", "currency", "kind" },
                unique: true,
                filter: "state IN ('OPEN','ACCEPTED')");

            migrationBuilder.CreateIndex(
                name: "ux_recon_run_number",
                schema: "fin",
                table: "recon_run",
                columns: new[] { "legal_entity_id", "definition_code", "period_code", "run_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recon_source_total_period",
                schema: "fin",
                table: "recon_source_total",
                columns: new[] { "legal_entity_id", "source", "period_code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recon_break",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "recon_source_total",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "recon_run",
                schema: "fin");
        }
    }
}
