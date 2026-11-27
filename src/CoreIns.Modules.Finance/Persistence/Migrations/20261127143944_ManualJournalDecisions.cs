using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualJournalDecisions : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_journal_entry_source_type",
                schema: "fin",
                table: "journal_entry");

            migrationBuilder.AddColumn<Guid>(
                name: "approval_request_id",
                schema: "fin",
                table: "manual_journal",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_journal_entry_source_type",
                schema: "fin",
                table: "journal_entry",
                sql: "source_type IN ('EVENT', 'REVERSAL', 'MANUAL')");

            // Lines are added only while the journal is a DRAFT: what is submitted is what the checker approves.
            migrationBuilder.Sql(FinanceSql.ManualJournalLinesDraftOnly);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_journal_entry_source_type",
                schema: "fin",
                table: "journal_entry");

            migrationBuilder.DropColumn(
                name: "approval_request_id",
                schema: "fin",
                table: "manual_journal");

            migrationBuilder.AddCheckConstraint(
                name: "ck_journal_entry_source_type",
                schema: "fin",
                table: "journal_entry",
                sql: "source_type IN ('EVENT', 'REVERSAL')");
        }
    }
}
