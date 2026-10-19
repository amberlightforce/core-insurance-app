using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Rating.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FallbackActivations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fallback_source_version",
                schema: "rat",
                table: "rate_activation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_event_id",
                schema: "rat",
                table: "rate_activation",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_rate_activation_source_event",
                schema: "rat",
                table: "rate_activation",
                column: "source_event_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_rate_activation_fallback",
                schema: "rat",
                table: "rate_activation",
                sql: "(fallback_source_version IS NULL) = (source_event_id IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_rate_activation_source_event",
                schema: "rat",
                table: "rate_activation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rate_activation_fallback",
                schema: "rat",
                table: "rate_activation");

            migrationBuilder.DropColumn(
                name: "fallback_source_version",
                schema: "rat",
                table: "rate_activation");

            migrationBuilder.DropColumn(
                name: "source_event_id",
                schema: "rat",
                table: "rate_activation");
        }
    }
}
