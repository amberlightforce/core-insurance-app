using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Migration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BatchBackgroundRun : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cursor",
                schema: "mig",
                table: "batch",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "failed_count",
                schema: "mig",
                table: "batch",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                schema: "mig",
                table: "batch",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "processed_count",
                schema: "mig",
                table: "batch",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_batch_status",
                schema: "mig",
                table: "batch",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_batch_status",
                schema: "mig",
                table: "batch");

            migrationBuilder.DropColumn(
                name: "cursor",
                schema: "mig",
                table: "batch");

            migrationBuilder.DropColumn(
                name: "failed_count",
                schema: "mig",
                table: "batch");

            migrationBuilder.DropColumn(
                name: "lease_until",
                schema: "mig",
                table: "batch");

            migrationBuilder.DropColumn(
                name: "processed_count",
                schema: "mig",
                table: "batch");
        }
    }
}
