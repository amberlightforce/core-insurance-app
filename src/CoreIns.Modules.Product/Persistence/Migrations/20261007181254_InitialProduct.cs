using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Product.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pfc");

            migrationBuilder.CreateTable(
                name: "artifact",
                schema: "pfc",
                columns: table => new
                {
                    artefact_hash = table.Column<string>(type: "char(64)", nullable: false),
                    canonical_json = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    stored_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifact", x => x.artefact_hash);
                    table.CheckConstraint("ck_artifact_hash", "artefact_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_artifact_size", "size_bytes > 0");
                });

            migrationBuilder.CreateTable(
                name: "product",
                schema: "pfc",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    line_code = table.Column<string>(type: "text", nullable: false),
                    line_family = table.Column<string>(type: "text", nullable: false),
                    name_el = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    product_type = table.Column<string>(type: "text", nullable: false),
                    customer_type = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product", x => x.product_id);
                    table.CheckConstraint("ck_product_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_product_record_version", "record_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "product_version",
                schema: "pfc",
                columns: table => new
                {
                    product_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    major = table.Column<int>(type: "integer", nullable: false),
                    minor = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    lifecycle_substate = table.Column<string>(type: "text", nullable: true),
                    is_abstract = table.Column<bool>(type: "boolean", nullable: false),
                    channels = table.Column<string[]>(type: "text[]", nullable: false),
                    contract_currency = table.Column<string>(type: "char(3)", nullable: false),
                    new_business_from = table.Column<DateOnly>(type: "date", nullable: false),
                    new_business_to = table.Column<DateOnly>(type: "date", nullable: true),
                    renewal_from = table.Column<DateOnly>(type: "date", nullable: false),
                    renewal_to = table.Column<DateOnly>(type: "date", nullable: true),
                    artefact_hash = table.Column<string>(type: "char(64)", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    locked_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_version", x => x.product_version_id);
                    table.CheckConstraint("ck_product_version_channels", "cardinality(channels) >= 1");
                    table.CheckConstraint("ck_product_version_hash", "artefact_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_product_version_nb_window", "new_business_to IS NULL OR new_business_to > new_business_from");
                    table.CheckConstraint("ck_product_version_number", "major >= 0 AND minor >= 0");
                    table.CheckConstraint("ck_product_version_renewal_window", "renewal_to IS NULL OR renewal_to > renewal_from");
                    table.CheckConstraint("ck_product_version_status", "status IN ('DRAFT', 'SUBMITTED', 'APPROVED', 'LOCKED', 'RETIRED')");
                    table.CheckConstraint("ck_product_version_substate", "lifecycle_substate IN ('ACTIVE', 'CLOSED_TO_NEW_BUSINESS', 'RUN_OFF')");
                    table.CheckConstraint("ck_product_version_substate_locked", "(status = 'LOCKED') = (lifecycle_substate IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_product_version_artifact",
                        column: x => x.artefact_hash,
                        principalSchema: "pfc",
                        principalTable: "artifact",
                        principalColumn: "artefact_hash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_version_product",
                        column: x => x.product_id,
                        principalSchema: "pfc",
                        principalTable: "product",
                        principalColumn: "product_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_product_code",
                schema: "pfc",
                table: "product",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_version_artefact_hash",
                schema: "pfc",
                table: "product_version",
                column: "artefact_hash");

            migrationBuilder.CreateIndex(
                name: "ix_product_version_resolution",
                schema: "pfc",
                table: "product_version",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_product_version_number",
                schema: "pfc",
                table: "product_version",
                columns: new[] { "product_id", "major", "minor" },
                unique: true);

            // BR-PFC-001: the new-business windows of Locked versions of one product never intersect.
            migrationBuilder.Sql(
                """
                ALTER TABLE pfc.product_version
                    ADD CONSTRAINT ex_product_version_locked_window
                    EXCLUDE USING gist (product_id WITH =, daterange(new_business_from, new_business_to, '[)') WITH &&)
                    WHERE (status = 'LOCKED')
                """);

            // REQ-PFC-197: a compiled artefact is write-once; the app role has UPDATE on the schema, so a trigger refuses it.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION pfc.refuse_artifact_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'pfc.artifact is write-once (REQ-PFC-197)' USING ERRCODE = 'integrity_constraint_violation';
                END $$;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_artifact_write_once BEFORE UPDATE OR DELETE ON pfc.artifact
                    FOR EACH ROW EXECUTE FUNCTION pfc.refuse_artifact_change()
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_artifact_write_once ON pfc.artifact");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS pfc.refuse_artifact_change()");

            migrationBuilder.DropTable(
                name: "product_version",
                schema: "pfc");

            migrationBuilder.DropTable(
                name: "artifact",
                schema: "pfc");

            migrationBuilder.DropTable(
                name: "product",
                schema: "pfc");
        }
    }
}
