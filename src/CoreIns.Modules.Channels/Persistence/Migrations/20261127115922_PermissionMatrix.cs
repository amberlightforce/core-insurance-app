using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Channels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PermissionMatrix : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permission_matrix",
                schema: "chn",
                columns: table => new
                {
                    matrix_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    scenarios = table.Column<string>(type: "jsonb", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    last_edited_by = table.Column<string>(type: "text", nullable: false),
                    submitted_by = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    effective_from = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permission_matrix", x => x.matrix_id);
                    table.CheckConstraint("ck_permission_matrix_status", "status IN ('DRAFT', 'SUBMITTED', 'SCHEDULED')");
                });

            migrationBuilder.CreateTable(
                name: "permission_matrix_entry",
                schema: "chn",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    matrix_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_type = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    product_line = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    max_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    level = table.Column<string>(type: "text", nullable: false),
                    removed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permission_matrix_entry", x => x.entry_id);
                    table.CheckConstraint("ck_permission_matrix_entry_level", "level IN ('EU', 'COUNTRY', 'LE')");
                    table.CheckConstraint("ck_permission_matrix_entry_max", "max_amount IS NULL OR max_amount >= 0");
                    table.CheckConstraint("ck_permission_matrix_entry_outcome", "outcome IN ('ALLOW', 'REFER', 'DENY')");
                    table.ForeignKey(
                        name: "fk_permission_matrix_entry_matrix",
                        column: x => x.matrix_id,
                        principalSchema: "chn",
                        principalTable: "permission_matrix",
                        principalColumn: "matrix_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_permission_matrix_effective",
                schema: "chn",
                table: "permission_matrix",
                columns: new[] { "legal_entity_id", "status", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ux_permission_matrix_version",
                schema: "chn",
                table: "permission_matrix",
                columns: new[] { "legal_entity_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_permission_matrix_entry_key",
                schema: "chn",
                table: "permission_matrix_entry",
                columns: new[] { "matrix_id", "transaction_type", "channel", "actor_type", "product_line" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "permission_matrix_entry",
                schema: "chn");

            migrationBuilder.DropTable(
                name: "permission_matrix",
                schema: "chn");
        }
    }
}
