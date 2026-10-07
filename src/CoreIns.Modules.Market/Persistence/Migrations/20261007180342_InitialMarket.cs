using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Market.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMarket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mkt");

            migrationBuilder.CreateTable(
                name: "legal_entity",
                schema: "mkt",
                columns: table => new
                {
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    native_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    latin_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    home_jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    pack_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    functional_currency = table.Column<string>(type: "char(3)", nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_test_entity = table.Column<bool>(type: "boolean", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_entity", x => x.legal_entity_id);
                    table.CheckConstraint("ck_legal_entity_functional_currency", "functional_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_legal_entity_home_jurisdiction", "home_jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_legal_entity_status", "status IN ('PLANNED', 'ONBOARDING', 'ACTIVE', 'RUN_OFF', 'CLOSED')");
                });

            migrationBuilder.CreateIndex(
                name: "ux_legal_entity_code",
                schema: "mkt",
                table: "legal_entity",
                column: "code",
                unique: true);

            // D-CON-33: the registry's one entity, the synthetic test entity of the local stack and the E2E run. The id is the
            // one the stack has always used for Stamp:LegalEntityId (infra/local/.env.example); it now lives here.
            migrationBuilder.Sql(
                """
                INSERT INTO mkt.legal_entity
                    (legal_entity_id, code, native_name, latin_name, home_jurisdiction, pack_id, functional_currency, timezone, status, is_test_entity, record_version, created_at)
                VALUES
                    ('0192f0c4-0000-7000-8000-000000000001', 'GR-TEST', 'GR-TEST (συνθετική οντότητα δοκιμών)', 'GR-TEST (synthetic test entity)',
                     'GR', 'gr', 'EUR', 'Europe/Athens', 'ACTIVE', true, 1, TIMESTAMPTZ '2026-10-07 00:00:00+00')
                ON CONFLICT (legal_entity_id) DO NOTHING
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_entity",
                schema: "mkt");
        }
    }
}
