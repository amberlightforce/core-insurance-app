using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllocationReversals : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_allocation_amount",
                schema: "bil",
                table: "allocation");

            migrationBuilder.AddColumn<string>(
                name: "comment",
                schema: "bil",
                table: "allocation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason_code",
                schema: "bil",
                table: "allocation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reverses_allocation_id",
                schema: "bil",
                table: "allocation",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_allocation_reverses",
                schema: "bil",
                table: "allocation",
                column: "reverses_allocation_id",
                unique: true,
                filter: "reverses_allocation_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_allocation_amount",
                schema: "bil",
                table: "allocation",
                sql: "amount <> 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_allocation_reversal",
                schema: "bil",
                table: "allocation",
                sql: "(reverses_allocation_id IS NULL) = (amount > 0)");

            migrationBuilder.AddForeignKey(
                name: "fk_allocation_reverses",
                schema: "bil",
                table: "allocation",
                column: "reverses_allocation_id",
                principalSchema: "bil",
                principalTable: "allocation",
                principalColumn: "allocation_id",
                onDelete: ReferentialAction.Restrict);

            // BO-BIL-07: the ALLOCATION_REVERSED ledger rule and the database guard of reversal rows.
            migrationBuilder.Sql(AllocationReversalSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AllocationReversalSql.Down);

            migrationBuilder.DropForeignKey(
                name: "fk_allocation_reverses",
                schema: "bil",
                table: "allocation");

            migrationBuilder.DropIndex(
                name: "ux_allocation_reverses",
                schema: "bil",
                table: "allocation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_allocation_amount",
                schema: "bil",
                table: "allocation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_allocation_reversal",
                schema: "bil",
                table: "allocation");

            migrationBuilder.DropColumn(
                name: "comment",
                schema: "bil",
                table: "allocation");

            migrationBuilder.DropColumn(
                name: "reason_code",
                schema: "bil",
                table: "allocation");

            migrationBuilder.DropColumn(
                name: "reverses_allocation_id",
                schema: "bil",
                table: "allocation");

            migrationBuilder.AddCheckConstraint(
                name: "ck_allocation_amount",
                schema: "bil",
                table: "allocation",
                sql: "amount > 0");
        }
    }
}
