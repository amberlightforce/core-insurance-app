using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntakeExceptionResolution : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_business_event_status",
                schema: "fin",
                table: "business_event");

            migrationBuilder.AddColumn<string>(
                name: "resolution_reason",
                schema: "fin",
                table: "business_event",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "resolved_at",
                schema: "fin",
                table: "business_event",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolved_by",
                schema: "fin",
                table: "business_event",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_business_event_status",
                schema: "fin",
                table: "business_event",
                sql: "status IN ('RECEIVED', 'WAITING', 'POSTED', 'NO_POSTING', 'SUSPENDED', 'DUPLICATE', 'REJECTED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_business_event_status",
                schema: "fin",
                table: "business_event");

            migrationBuilder.DropColumn(
                name: "resolution_reason",
                schema: "fin",
                table: "business_event");

            migrationBuilder.DropColumn(
                name: "resolved_at",
                schema: "fin",
                table: "business_event");

            migrationBuilder.DropColumn(
                name: "resolved_by",
                schema: "fin",
                table: "business_event");

            migrationBuilder.AddCheckConstraint(
                name: "ck_business_event_status",
                schema: "fin",
                table: "business_event",
                sql: "status IN ('RECEIVED', 'WAITING', 'POSTED', 'NO_POSTING', 'SUSPENDED')");
        }
    }
}
