using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CessionDelta : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cession",
                schema: "ri",
                columns: table => new
                {
                    cession_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    treaty_id = table.Column<string>(type: "text", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_key = table.Column<Guid>(type: "uuid", nullable: false),
                    written_premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    target = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    booked_before = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    delta = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cession", x => x.cession_id);
                    table.CheckConstraint("ck_cession_mode", "mode IN ('NET', 'GROSS')");
                });

            migrationBuilder.CreateTable(
                name: "cession_exception",
                schema: "ri",
                columns: table => new
                {
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cession_exception", x => x.exception_id);
                });

            migrationBuilder.CreateTable(
                name: "premium_delta",
                schema: "ri",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coverage_code = table.Column<string>(type: "text", nullable: false),
                    charge_category = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_size = table.Column<int>(type: "integer", nullable: false),
                    correction_of = table.Column<Guid>(type: "uuid", nullable: true),
                    oos_set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_premium_delta", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "tracker",
                schema: "ri",
                columns: table => new
                {
                    tracker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    treaty_id = table.Column<string>(type: "text", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    opening_aggregate = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tracker", x => x.tracker_id);
                    table.CheckConstraint("ck_tracker_amount", "opening_aggregate >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ux_cession_set",
                schema: "ri",
                table: "cession",
                columns: new[] { "term_id", "treaty_id", "set_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cession_exception_term",
                schema: "ri",
                table: "cession_exception",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "ix_premium_delta_set",
                schema: "ri",
                table: "premium_delta",
                column: "set_id");

            migrationBuilder.CreateIndex(
                name: "ix_premium_delta_term",
                schema: "ri",
                table: "premium_delta",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "ux_tracker_treaty_year",
                schema: "ri",
                table: "tracker",
                columns: new[] { "legal_entity_id", "treaty_id", "contract_year" },
                unique: true);
            migrationBuilder.Sql(Cession.CessionDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Cession.CessionDatabaseSql.Down);
            migrationBuilder.DropTable(
                name: "cession",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "cession_exception",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "premium_delta",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "tracker",
                schema: "ri");
        }
    }
}
