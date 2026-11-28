using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Notes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "note",
                schema: "wrk",
                columns: table => new
                {
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_module = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    object_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    object_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    search_key = table.Column<string>(type: "text", nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    deleted_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    delete_reason = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_note", x => x.note_id);
                    table.CheckConstraint("ck_note_deleted", "(deleted_at IS NULL) = (deleted_by IS NULL)");
                    table.CheckConstraint("ck_note_visibility", "visibility IN ('STAFF', 'MANAGERS', 'PRIVATE')");
                });

            migrationBuilder.CreateTable(
                name: "note_version",
                schema: "wrk",
                columns: table => new
                {
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    edited_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    edited_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_note_version", x => new { x.note_id, x.version });
                    table.ForeignKey(
                        name: "fk_note_version_note",
                        column: x => x.note_id,
                        principalSchema: "wrk",
                        principalTable: "note",
                        principalColumn: "note_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_note_object",
                schema: "wrk",
                table: "note",
                columns: new[] { "legal_entity_id", "object_module", "object_type", "object_id", "created_at" });

            // The history is append-only: no UPDATE or DELETE on a stored version, whatever the role.
            migrationBuilder.Sql("""
                CREATE FUNCTION wrk.note_version_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'wrk.note_version is append-only';
                END $$;
                CREATE TRIGGER trg_note_version_append_only BEFORE UPDATE OR DELETE ON wrk.note_version
                    FOR EACH ROW EXECUTE FUNCTION wrk.note_version_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_note_version_append_only ON wrk.note_version; DROP FUNCTION IF EXISTS wrk.note_version_append_only();");

            migrationBuilder.DropTable(
                name: "note_version",
                schema: "wrk");

            migrationBuilder.DropTable(
                name: "note",
                schema: "wrk");
        }
    }
}
