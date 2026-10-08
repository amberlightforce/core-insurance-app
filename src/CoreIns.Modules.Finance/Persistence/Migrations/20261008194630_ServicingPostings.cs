using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServicingPostings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "refund_id",
                schema: "fin",
                table: "journal_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_refund",
                schema: "fin",
                table: "journal_line",
                columns: new[] { "legal_entity_id", "refund_id", "account_code" },
                filter: "refund_id IS NOT NULL");

            // SL3-FIN-RULES (D-SL3-05): rule set v3 (seed gr-test.finance.v3) for credits, refunds and refund disbursements,
            // effective from 2026-10-01. v2 stays Active for earlier dates. Seed rows are inserted ON CONFLICT DO NOTHING,
            // so the chart, derivation and catalogue rows of v1 and v2 are kept.
            migrationBuilder.Sql(FinanceSeed.Sql(FinanceSeed.GrTestV3));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rule rows are append-only (REQ-FIN-070): Down supersedes v3 instead of deleting it.
            migrationBuilder.Sql(FinanceSeed.SupersedeV3);

            migrationBuilder.DropIndex(
                name: "ix_journal_line_refund",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "refund_id",
                schema: "fin",
                table: "journal_line");
        }
    }
}
