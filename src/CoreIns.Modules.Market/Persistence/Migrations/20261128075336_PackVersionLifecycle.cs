using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreIns.Modules.Market.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PackVersionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pack_version_event",
                schema: "mkt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    pack_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    production_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pack_version_event", x => x.id);
                    table.CheckConstraint("ck_pack_version_event_status", "status IN ('Signed', 'Certified', 'Rejected', 'Published', 'Deprecated', 'Removed')");
                    table.ForeignKey(
                        name: "fk_pack_version_event_pack_version",
                        columns: x => new { x.pack_id, x.version },
                        principalSchema: "mkt",
                        principalTable: "pack_version",
                        principalColumns: new[] { "pack_id", "version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pack_version_event_version",
                schema: "mkt",
                table: "pack_version_event",
                columns: new[] { "pack_id", "version", "seq" });

            // Append-only like pack_version (D-SL5-06): the lifecycle history is never rewritten.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER tr_pack_version_event_append_only BEFORE UPDATE OR DELETE ON mkt.pack_version_event
                    FOR EACH ROW EXECUTE FUNCTION mkt.reject_state_change();
                CREATE TRIGGER tr_pack_version_event_no_truncate BEFORE TRUNCATE ON mkt.pack_version_event
                    FOR EACH STATEMENT EXECUTE FUNCTION mkt.reject_state_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pack_version_event",
                schema: "mkt");
        }
    }
}
