using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProgrammeApproval : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "approval_request_id",
                schema: "ri",
                table: "programme_version",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                schema: "ri",
                table: "programme_version",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "decided_at",
                schema: "ri",
                table: "programme_version",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decided_by",
                schema: "ri",
                table: "programme_version",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "return_reason",
                schema: "ri",
                table: "programme_version",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "submitted_at",
                schema: "ri",
                table: "programme_version",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "submitted_by",
                schema: "ri",
                table: "programme_version",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "approval_request_id",
                schema: "ri",
                table: "programme_version");

            migrationBuilder.DropColumn(
                name: "content_hash",
                schema: "ri",
                table: "programme_version");

            migrationBuilder.DropColumn(
                name: "decided_at",
                schema: "ri",
                table: "programme_version");

            migrationBuilder.DropColumn(
                name: "decided_by",
                schema: "ri",
                table: "programme_version");

            migrationBuilder.DropColumn(
                name: "return_reason",
                schema: "ri",
                table: "programme_version");

            migrationBuilder.DropColumn(
                name: "submitted_at",
                schema: "ri",
                table: "programme_version");

            migrationBuilder.DropColumn(
                name: "submitted_by",
                schema: "ri",
                table: "programme_version");
        }
    }
}
