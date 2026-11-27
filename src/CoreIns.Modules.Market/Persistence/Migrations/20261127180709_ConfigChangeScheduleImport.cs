using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Market.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConfigChangeScheduleImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_config_change_request_status",
                schema: "mkt",
                table: "config_change_request");

            migrationBuilder.AddColumn<string>(
                name: "cause",
                schema: "mkt",
                table: "config_change_request",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "MANUAL");

            migrationBuilder.AddColumn<DateTime>(
                name: "effective_at",
                schema: "mkt",
                table: "config_change_request",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_digest",
                schema: "mkt",
                table: "config_change_request",
                type: "char(64)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_config_change_request_due",
                schema: "mkt",
                table: "config_change_request",
                columns: new[] { "status", "effective_at" });

            migrationBuilder.CreateIndex(
                name: "ix_config_change_request_import",
                schema: "mkt",
                table: "config_change_request",
                columns: new[] { "legal_entity_id", "import_digest" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_config_change_request_cause",
                schema: "mkt",
                table: "config_change_request",
                sql: "cause IN ('MANUAL', 'IMPORT')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_config_change_request_status",
                schema: "mkt",
                table: "config_change_request",
                sql: "status IN ('DRAFT', 'PENDING_APPROVAL', 'SCHEDULED', 'APPLIED', 'REJECTED', 'WITHDRAWN')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_config_change_request_due",
                schema: "mkt",
                table: "config_change_request");

            migrationBuilder.DropIndex(
                name: "ix_config_change_request_import",
                schema: "mkt",
                table: "config_change_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_config_change_request_cause",
                schema: "mkt",
                table: "config_change_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_config_change_request_status",
                schema: "mkt",
                table: "config_change_request");

            migrationBuilder.DropColumn(
                name: "cause",
                schema: "mkt",
                table: "config_change_request");

            migrationBuilder.DropColumn(
                name: "effective_at",
                schema: "mkt",
                table: "config_change_request");

            migrationBuilder.DropColumn(
                name: "import_digest",
                schema: "mkt",
                table: "config_change_request");

            migrationBuilder.AddCheckConstraint(
                name: "ck_config_change_request_status",
                schema: "mkt",
                table: "config_change_request",
                sql: "status IN ('DRAFT', 'PENDING_APPROVAL', 'APPLIED', 'REJECTED', 'WITHDRAWN')");
        }
    }
}
