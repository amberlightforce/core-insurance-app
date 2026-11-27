using System;
using Microsoft.EntityFrameworkCore.Migrations;
using CoreIns.Modules.Reinsurance.Persistence;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Statements : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "statement",
                schema: "ri",
                columns: table => new
                {
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reinsurer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    previous_statement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    opening = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    commission = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    recoveries = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    movement = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    closing = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    submitted_by = table.Column<string>(type: "text", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    issued_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    issued_by = table.Column<string>(type: "text", nullable: true),
                    response = table.Column<string>(type: "text", nullable: true),
                    response_reason = table.Column<string>(type: "text", nullable: true),
                    responded_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    responded_by = table.Column<string>(type: "text", nullable: true),
                    superseded_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement", x => x.statement_id);
                    table.CheckConstraint("ck_statement_period", "period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                    table.CheckConstraint("ck_statement_response", "response IS NULL OR response IN ('AGREED', 'DISPUTED')");
                    table.CheckConstraint("ck_statement_status", "status IN ('DRAFT', 'SUBMITTED', 'APPROVED', 'ISSUED', 'AGREED', 'DISPUTED', 'SUPERSEDED')");
                });

            migrationBuilder.CreateTable(
                name: "statement_line",
                schema: "ri",
                columns: table => new
                {
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    source_kind = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    item_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_line", x => new { x.statement_id, x.line_no });
                    table.ForeignKey(
                        name: "fk_statement_line_statement",
                        column: x => x.statement_id,
                        principalSchema: "ri",
                        principalTable: "statement",
                        principalColumn: "statement_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_statement_live",
                schema: "ri",
                table: "statement",
                columns: new[] { "legal_entity_id", "reinsurer_party_id", "period", "currency" },
                unique: true,
                filter: "status <> 'SUPERSEDED'");

            migrationBuilder.CreateIndex(
                name: "ux_statement_version",
                schema: "ri",
                table: "statement",
                columns: new[] { "legal_entity_id", "reinsurer_party_id", "period", "currency", "version" },
                unique: true);

            migrationBuilder.Sql(StatementDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(StatementDatabaseSql.Down);
            migrationBuilder.DropTable(
                name: "statement_line",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "statement",
                schema: "ri");
        }
    }
}
