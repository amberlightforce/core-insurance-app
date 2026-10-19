using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OosGuard : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_period_status",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.AddColumn<Guid>(
                name: "correction_of",
                schema: "fin",
                table: "journal_entry",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "oos_set_id",
                schema: "fin",
                table: "journal_entry",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_period_status",
                schema: "fin",
                table: "financial_period",
                sql: "status IN ('OPEN', 'LOCKED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_period_status",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.DropColumn(
                name: "correction_of",
                schema: "fin",
                table: "journal_entry");

            migrationBuilder.DropColumn(
                name: "oos_set_id",
                schema: "fin",
                table: "journal_entry");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_period_status",
                schema: "fin",
                table: "financial_period",
                sql: "status IN ('OPEN')");
        }
    }
}
