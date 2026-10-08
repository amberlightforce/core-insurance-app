using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClaimsPostings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "claim_id",
                schema: "fin",
                table: "journal_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "claim_payment_id",
                schema: "fin",
                table: "journal_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_category",
                schema: "fin",
                table: "journal_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_type",
                schema: "fin",
                table: "journal_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "disbursement_id",
                schema: "fin",
                table: "journal_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "exposure_id",
                schema: "fin",
                table: "journal_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reserve_line_id",
                schema: "fin",
                table: "journal_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_claim",
                schema: "fin",
                table: "journal_line",
                columns: new[] { "legal_entity_id", "claim_id" },
                filter: "claim_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_claim_payment",
                schema: "fin",
                table: "journal_line",
                columns: new[] { "legal_entity_id", "claim_payment_id", "account_code" },
                filter: "claim_payment_id IS NOT NULL");

            // SL2-FIN-CLM (D-SL2-08): the claims chart accounts, catalogue entries and rule set v2 (seed gr-test.finance.v2),
            // then v1 Superseded. Seed rows are inserted ON CONFLICT DO NOTHING, so v1's chart and catalogue rows are kept.
            migrationBuilder.Sql(FinanceSeed.Sql(FinanceSeed.GrTestV2));
            migrationBuilder.Sql(FinanceSeed.SupersedeV1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rule rows are append-only (REQ-FIN-070): Down re-activates v1 and supersedes v2 instead of deleting them.
            migrationBuilder.Sql(FinanceSeed.ReactivateV1);

            migrationBuilder.DropIndex(
                name: "ix_journal_line_claim",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropIndex(
                name: "ix_journal_line_claim_payment",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "claim_id",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "claim_payment_id",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "cost_category",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "cost_type",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "disbursement_id",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "exposure_id",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "reserve_line_id",
                schema: "fin",
                table: "journal_line");
        }
    }
}
