using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreIns.Modules.Market.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurationStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "config_state",
                schema: "mkt",
                columns: table => new
                {
                    hash = table.Column<string>(type: "char(64)", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    parent_hash = table.Column<string>(type: "char(64)", nullable: true),
                    manifest = table.Column<string>(type: "jsonb", nullable: false),
                    activated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    cause = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    cause_ref = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_config_state", x => x.hash);
                    table.CheckConstraint("ck_config_state_cause", "cause IN ('GENESIS', 'PACK_ACTIVATION', 'PACK_ROLLBACK')");
                    table.CheckConstraint("ck_config_state_hash", "hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_config_state_parent", "(cause = 'GENESIS') = (parent_hash IS NULL)");
                    table.ForeignKey(
                        name: "fk_config_state_parent",
                        column: x => x.parent_hash,
                        principalSchema: "mkt",
                        principalTable: "config_state",
                        principalColumn: "hash");
                });

            migrationBuilder.CreateTable(
                name: "pack_version",
                schema: "mkt",
                columns: table => new
                {
                    pack_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    country = table.Column<string>(type: "char(2)", nullable: true),
                    content_digest = table.Column<string>(type: "char(64)", nullable: false),
                    values = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    registered_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pack_version", x => new { x.pack_id, x.version });
                    table.CheckConstraint("ck_pack_version_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_pack_version_semver", "version ~ '^[0-9]+[.][0-9]+[.][0-9]+$'");
                    table.CheckConstraint("ck_pack_version_status", "status IN ('Built', 'Signed', 'Certified', 'Rejected', 'Published', 'Deprecated', 'Removed')");
                });

            migrationBuilder.CreateTable(
                name: "pack_activation",
                schema: "mkt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pack_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    decided_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    resulting_hash = table.Column<string>(type: "char(64)", nullable: true),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pack_activation", x => x.id);
                    table.CheckConstraint("ck_pack_activation_kind", "kind IN ('ACTIVATE', 'ROLLBACK')");
                    table.CheckConstraint("ck_pack_activation_status", "status IN ('PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'WITHDRAWN', 'ACTIVE', 'SUPERSEDED')");
                    table.ForeignKey(
                        name: "fk_pack_activation_legal_entity",
                        column: x => x.legal_entity_id,
                        principalSchema: "mkt",
                        principalTable: "legal_entity",
                        principalColumn: "legal_entity_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pack_activation_pack_version",
                        columns: x => new { x.pack_id, x.version },
                        principalSchema: "mkt",
                        principalTable: "pack_version",
                        principalColumns: new[] { "pack_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pack_activation_state",
                        column: x => x.resulting_hash,
                        principalSchema: "mkt",
                        principalTable: "config_state",
                        principalColumn: "hash");
                    table.ForeignKey(
                        name: "fk_pack_activation_supersedes",
                        column: x => x.supersedes_id,
                        principalSchema: "mkt",
                        principalTable: "pack_activation",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ux_config_state_parent",
                schema: "mkt",
                table: "config_state",
                column: "parent_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_config_state_seq",
                schema: "mkt",
                table: "config_state",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pack_activation_entity_pack",
                schema: "mkt",
                table: "pack_activation",
                columns: new[] { "legal_entity_id", "pack_id", "activated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_pack_activation_pack_id_version",
                schema: "mkt",
                table: "pack_activation",
                columns: new[] { "pack_id", "version" });

            migrationBuilder.CreateIndex(
                name: "IX_pack_activation_resulting_hash",
                schema: "mkt",
                table: "pack_activation",
                column: "resulting_hash");

            migrationBuilder.CreateIndex(
                name: "IX_pack_activation_supersedes_id",
                schema: "mkt",
                table: "pack_activation",
                column: "supersedes_id");

            // D-SL5-06/07: append-only states and pack versions, linear chain written under the state lock.
            migrationBuilder.Sql(MarketStateSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(MarketStateSql.Down);

            migrationBuilder.DropTable(
                name: "pack_activation",
                schema: "mkt");

            migrationBuilder.DropTable(
                name: "pack_version",
                schema: "mkt");

            migrationBuilder.DropTable(
                name: "config_state",
                schema: "mkt");
        }
    }
}
