using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CessionTreatyRegistry : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cession_treaty",
                schema: "ri",
                columns: table => new
                {
                    treaty_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    treaty_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    share_pct = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    cession_base = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    coverage_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    known_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    known_to = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cession_treaty", x => x.treaty_version_id);
                    table.CheckConstraint("ck_cession_treaty_base", "cession_base IN ('NET', 'GROSS')");
                    table.CheckConstraint("ck_cession_treaty_kind", "kind IN ('QUOTA_SHARE', 'XOL_NO_CESSION')");
                    table.CheckConstraint("ck_cession_treaty_known", "known_to IS NULL OR known_to > known_from");
                    table.CheckConstraint("ck_cession_treaty_period", "valid_to > valid_from");
                    table.CheckConstraint("ck_cession_treaty_scope", "cardinality(coverage_codes) >= 1");
                    table.CheckConstraint("ck_cession_treaty_share", "share_pct >= 0 AND share_pct <= 100");
                    table.CheckConstraint("ck_cession_treaty_source", "source IN ('REGISTRY', 'DEV_SEED')");
                    table.CheckConstraint("ck_cession_treaty_status", "status IN ('DRAFT', 'APPROVED', 'ACTIVE', 'WITHDRAWN')");
                });

            migrationBuilder.CreateIndex(
                name: "ux_cession_treaty_current",
                schema: "ri",
                table: "cession_treaty",
                columns: new[] { "legal_entity_id", "treaty_id" },
                unique: true,
                filter: "known_to IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cession_treaty",
                schema: "ri");
        }
    }
}
