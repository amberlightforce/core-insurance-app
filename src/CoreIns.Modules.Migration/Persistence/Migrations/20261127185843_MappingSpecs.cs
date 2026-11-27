using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Migration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MappingSpecs : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mapping_spec",
                schema: "mig",
                columns: table => new
                {
                    spec_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    source_object_type = table.Column<string>(type: "text", nullable: false),
                    target_module = table.Column<string>(type: "text", nullable: false),
                    target_object_type = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_comment = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mapping_spec", x => x.spec_id);
                    table.CheckConstraint("ck_mapping_spec_status", "status IN ('DRAFT','PENDING_APPROVAL','APPROVED','REJECTED')");
                });

            migrationBuilder.CreateTable(
                name: "code_map",
                schema: "mig",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    spec_id = table.Column<Guid>(type: "uuid", nullable: false),
                    map_name = table.Column<string>(type: "text", nullable: false),
                    legacy_value = table.Column<string>(type: "text", nullable: false),
                    core_value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_map", x => x.entry_id);
                    table.ForeignKey(
                        name: "fk_code_map_spec",
                        column: x => x.spec_id,
                        principalSchema: "mig",
                        principalTable: "mapping_spec",
                        principalColumn: "spec_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_code_map_entry",
                schema: "mig",
                table: "code_map",
                columns: new[] { "spec_id", "map_name", "legacy_value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_mapping_spec_name_version",
                schema: "mig",
                table: "mapping_spec",
                columns: new[] { "legal_entity_id", "name", "version" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION mig.mapping_spec_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.spec_id <> OLD.spec_id OR NEW.legal_entity_id <> OLD.legal_entity_id OR NEW.name <> OLD.name OR NEW.version <> OLD.version
                        OR NEW.source_object_type <> OLD.source_object_type OR NEW.target_module <> OLD.target_module OR NEW.target_object_type <> OLD.target_object_type
                        OR NEW.fields <> OLD.fields OR NEW.content_hash <> OLD.content_hash OR NEW.created_by <> OLD.created_by OR NEW.created_at <> OLD.created_at
                        OR NEW.description IS DISTINCT FROM OLD.description THEN
                        RAISE EXCEPTION 'The content of a mapping spec version is immutable; create a new version.';
                    END IF;
                    IF NOT ((OLD.status = 'DRAFT' AND NEW.status = 'PENDING_APPROVAL') OR (OLD.status = 'PENDING_APPROVAL' AND NEW.status IN ('APPROVED', 'REJECTED'))) THEN
                        RAISE EXCEPTION 'A % mapping spec cannot become %.', OLD.status, NEW.status;
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER mapping_spec_guard BEFORE UPDATE ON mig.mapping_spec FOR EACH ROW EXECUTE FUNCTION mig.mapping_spec_guard();
                CREATE FUNCTION mig.code_map_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'Code table entries are never changed or deleted; create a new spec version.';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM mig.mapping_spec WHERE spec_id = NEW.spec_id AND status = 'DRAFT') THEN
                        RAISE EXCEPTION 'Code table entries can only be added to a draft spec.';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER code_map_guard BEFORE INSERT OR UPDATE OR DELETE ON mig.code_map FOR EACH ROW EXECUTE FUNCTION mig.code_map_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS code_map_guard ON mig.code_map; DROP TRIGGER IF EXISTS mapping_spec_guard ON mig.mapping_spec; DROP FUNCTION IF EXISTS mig.code_map_guard(); DROP FUNCTION IF EXISTS mig.mapping_spec_guard();");

            migrationBuilder.DropTable(
                name: "code_map",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "mapping_spec",
                schema: "mig");
        }
    }
}
