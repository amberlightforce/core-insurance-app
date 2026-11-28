using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CessionRerun : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "amount",
                schema: "ri",
                table: "cession_exception",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "charge_category",
                schema: "ri",
                table: "cession_exception",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "correction_of",
                schema: "ri",
                table: "cession_exception",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "coverage_code",
                schema: "ri",
                table: "cession_exception",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "ri",
                table: "cession_exception",
                type: "char(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "oos_set_id",
                schema: "ri",
                table: "cession_exception",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "policy_id",
                schema: "ri",
                table: "cession_exception",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "set_id",
                schema: "ri",
                table: "cession_exception",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "set_size",
                schema: "ri",
                table: "cession_exception",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "valid_from",
                schema: "ri",
                table: "cession_exception",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "exception_id",
                schema: "ri",
                table: "cession",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                schema: "ri",
                table: "cession",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cession_exception_state",
                schema: "ri",
                columns: table => new
                {
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    assignee = table.Column<string>(type: "text", nullable: true),
                    assigned_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    assigned_by = table.Column<string>(type: "text", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    resolved_by = table.Column<string>(type: "text", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    resolution_reason = table.Column<string>(type: "text", nullable: true),
                    rerun_count = table.Column<int>(type: "integer", nullable: false),
                    last_rerun_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cession_exception_state", x => x.exception_id);
                    table.CheckConstraint("ck_cession_exception_state_status", "status IN ('OPEN', 'ASSIGNED', 'RESOLVED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_cession_exception_link",
                schema: "ri",
                table: "cession",
                column: "exception_id");

            migrationBuilder.CreateIndex(
                name: "ix_cession_exception_state_status",
                schema: "ri",
                table: "cession_exception_state",
                columns: new[] { "legal_entity_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cession_exception_state",
                schema: "ri");

            migrationBuilder.DropIndex(
                name: "ix_cession_exception_link",
                schema: "ri",
                table: "cession");

            migrationBuilder.DropColumn(
                name: "amount",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "charge_category",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "correction_of",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "coverage_code",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "oos_set_id",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "policy_id",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "set_id",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "set_size",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "valid_from",
                schema: "ri",
                table: "cession_exception");

            migrationBuilder.DropColumn(
                name: "exception_id",
                schema: "ri",
                table: "cession");

            migrationBuilder.DropColumn(
                name: "run_id",
                schema: "ri",
                table: "cession");
        }
    }
}
