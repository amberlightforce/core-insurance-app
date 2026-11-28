using System;
using Microsoft.EntityFrameworkCore.Migrations;
using CoreIns.Modules.Reinsurance.Persistence;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Settlements : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "settlement",
                schema: "ri",
                columns: table => new
                {
                    settlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reinsurer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    period = table.Column<string>(type: "text", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    statement_closing = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    net = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    proposed_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    proposed_by = table.Column<string>(type: "text", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    approval_comment = table.Column<string>(type: "text", nullable: true),
                    authority_check_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settled_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    settled_by = table.Column<string>(type: "text", nullable: true),
                    value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    bank_reference = table.Column<string>(type: "text", nullable: true),
                    evidence_ref = table.Column<string>(type: "text", nullable: true),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bil_reference = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlement", x => x.settlement_id);
                    table.CheckConstraint("ck_settlement_amount", "amount > 0 AND amount = abs(net)");
                    table.CheckConstraint("ck_settlement_direction", "direction IN ('PAY', 'RECEIVE')");
                    table.CheckConstraint("ck_settlement_status", "status IN ('PROPOSED', 'APPROVED', 'RELEASED', 'RECORDED', 'SUPERSEDED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_settlement_status",
                schema: "ri",
                table: "settlement",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_settlement_live",
                schema: "ri",
                table: "settlement",
                columns: new[] { "legal_entity_id", "reinsurer_party_id", "currency" },
                unique: true,
                filter: "status IN ('PROPOSED', 'APPROVED')");

            migrationBuilder.CreateIndex(
                name: "ux_settlement_reference",
                schema: "ri",
                table: "settlement",
                columns: new[] { "legal_entity_id", "reinsurer_party_id", "bank_reference" },
                unique: true,
                filter: "bank_reference IS NOT NULL");
            migrationBuilder.Sql(SettlementDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SettlementDatabaseSql.Down);
            migrationBuilder.DropTable(
                name: "settlement",
                schema: "ri");
        }
    }
}
