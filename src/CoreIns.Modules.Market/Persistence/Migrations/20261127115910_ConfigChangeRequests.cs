using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Market.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConfigChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_config_state_cause",
                schema: "mkt",
                table: "config_state");

            migrationBuilder.CreateTable(
                name: "config_change_request",
                schema: "mkt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country = table.Column<string>(type: "char(2)", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    base_hash = table.Column<string>(type: "char(64)", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_principal = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decided_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_hash = table.Column<string>(type: "char(64)", nullable: true),
                    resulting_hash = table.Column<string>(type: "char(64)", nullable: true),
                    applied_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_config_change_request", x => x.id);
                    table.CheckConstraint("ck_config_change_request_base_hash", "base_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_config_change_request_status", "status IN ('DRAFT', 'PENDING_APPROVAL', 'APPLIED', 'REJECTED', 'WITHDRAWN')");
                    table.ForeignKey(
                        name: "fk_config_change_request_legal_entity",
                        column: x => x.legal_entity_id,
                        principalSchema: "mkt",
                        principalTable: "legal_entity",
                        principalColumn: "legal_entity_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_config_change_request_state",
                        column: x => x.resulting_hash,
                        principalSchema: "mkt",
                        principalTable: "config_state",
                        principalColumn: "hash");
                });

            migrationBuilder.CreateTable(
                name: "config_change_item",
                schema: "mkt",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    legal_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    motor_path = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_config_change_item", x => new { x.request_id, x.line_no });
                    table.ForeignKey(
                        name: "fk_config_change_item_request",
                        column: x => x.request_id,
                        principalSchema: "mkt",
                        principalTable: "config_change_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_config_state_cause",
                schema: "mkt",
                table: "config_state",
                sql: "cause IN ('GENESIS', 'PACK_ACTIVATION', 'PACK_ROLLBACK', 'CONFIG_CHANGE')");

            migrationBuilder.CreateIndex(
                name: "ix_config_change_request_entity_status",
                schema: "mkt",
                table: "config_change_request",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_config_change_request_resulting_hash",
                schema: "mkt",
                table: "config_change_request",
                column: "resulting_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "config_change_item",
                schema: "mkt");

            migrationBuilder.DropTable(
                name: "config_change_request",
                schema: "mkt");

            migrationBuilder.DropCheckConstraint(
                name: "ck_config_state_cause",
                schema: "mkt",
                table: "config_state");

            migrationBuilder.AddCheckConstraint(
                name: "ck_config_state_cause",
                schema: "mkt",
                table: "config_state",
                sql: "cause IN ('GENESIS', 'PACK_ACTIVATION', 'PACK_ROLLBACK')");
        }
    }
}
