using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryPostings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "counterparty_party_id",
                schema: "fin",
                table: "journal_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fs_statement_ref",
                schema: "fin",
                table: "journal_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recoverable_basis",
                schema: "fin",
                table: "journal_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "recovery_id",
                schema: "fin",
                table: "journal_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ri_contract_id",
                schema: "fin",
                table: "journal_line",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ri_contract_year",
                schema: "fin",
                table: "journal_line",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ri_held_group",
                schema: "fin",
                table: "journal_line",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_fs_statement",
                schema: "fin",
                table: "journal_line",
                columns: new[] { "legal_entity_id", "counterparty_party_id", "fs_statement_ref", "account_code" },
                filter: "fs_statement_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_recovery",
                schema: "fin",
                table: "journal_line",
                columns: new[] { "legal_entity_id", "recovery_id", "account_code" },
                filter: "recovery_id IS NOT NULL");

            // SL4-FIN-V4 (D-SL4-10): rule set v4 (seed gr-test.finance.v4), effective from 2026-10-01 like v3 and winning the tie by
            // version number. v1-v3 stay in the table; journals keep the rule-set version they were posted with.
            migrationBuilder.Sql(FinanceSeed.Sql(FinanceSeed.GrTestV4));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rule rows are append-only (REQ-FIN-070): Down supersedes v4 instead of deleting it.
            migrationBuilder.Sql(FinanceSeed.SupersedeV4);

            migrationBuilder.DropIndex(
                name: "ix_journal_line_fs_statement",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropIndex(
                name: "ix_journal_line_recovery",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "counterparty_party_id",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "fs_statement_ref",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "recoverable_basis",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "recovery_id",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "ri_contract_id",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "ri_contract_year",
                schema: "fin",
                table: "journal_line");

            migrationBuilder.DropColumn(
                name: "ri_held_group",
                schema: "fin",
                table: "journal_line");
        }
    }
}
