using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PeriodClose : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_period_status",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.AddColumn<int>(
                name: "lock_count",
                schema: "fin",
                table: "financial_period",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "locked_at",
                schema: "fin",
                table: "financial_period",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "locked_by",
                schema: "fin",
                table: "financial_period",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reopen_reason",
                schema: "fin",
                table: "financial_period",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "reopen_requested_at",
                schema: "fin",
                table: "financial_period",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reopen_requested_by",
                schema: "fin",
                table: "financial_period",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "soft_closed_at",
                schema: "fin",
                table: "financial_period",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "soft_closed_by",
                schema: "fin",
                table: "financial_period",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "period_close_task",
                schema: "fin",
                columns: table => new
                {
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    signed_off_by = table.Column<string>(type: "text", nullable: true),
                    signed_off_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    break_accepted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_period_close_task", x => new { x.period_id, x.task_code });
                    table.CheckConstraint("ck_period_close_task_status", "status IN ('OPEN', 'SIGNED_OFF')");
                    table.ForeignKey(
                        name: "fk_period_close_task_period",
                        column: x => x.period_id,
                        principalSchema: "fin",
                        principalTable: "financial_period",
                        principalColumn: "period_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_period_status",
                schema: "fin",
                table: "financial_period",
                sql: "status IN ('OPEN', 'SOFT_CLOSED', 'LOCKED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "period_close_task",
                schema: "fin");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_period_status",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.DropColumn(
                name: "lock_count",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.DropColumn(
                name: "locked_at",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.DropColumn(
                name: "locked_by",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.DropColumn(
                name: "reopen_reason",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.DropColumn(
                name: "reopen_requested_at",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.DropColumn(
                name: "reopen_requested_by",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.DropColumn(
                name: "soft_closed_at",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.DropColumn(
                name: "soft_closed_by",
                schema: "fin",
                table: "financial_period");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_period_status",
                schema: "fin",
                table: "financial_period",
                sql: "status IN ('OPEN', 'LOCKED')");
        }
    }
}
