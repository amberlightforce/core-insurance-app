using System;
using Microsoft.EntityFrameworkCore.Migrations;
using CoreIns.Modules.Reinsurance.Persistence;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityRulesLineTables : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "line_table",
                schema: "ri",
                columns: table => new
                {
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    rows = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_line_table", x => x.table_id);
                });

            migrationBuilder.CreateTable(
                name: "security_rule",
                schema: "ri",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    rating_floor = table.Column<string>(type: "text", nullable: true),
                    max_share_pct = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    ratings = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_rule", x => x.rule_id);
                    table.CheckConstraint("ck_security_rule_share", "max_share_pct IS NULL OR (max_share_pct > 0 AND max_share_pct <= 100)");
                });

            migrationBuilder.CreateIndex(
                name: "ux_line_table_version",
                schema: "ri",
                table: "line_table",
                columns: new[] { "legal_entity_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_security_rule_version",
                schema: "ri",
                table: "security_rule",
                columns: new[] { "legal_entity_id", "version_no" },
                unique: true);
            migrationBuilder.Sql(SecurityDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecurityDatabaseSql.Down);
            migrationBuilder.DropTable(
                name: "line_table",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "security_rule",
                schema: "ri");
        }
    }
}
