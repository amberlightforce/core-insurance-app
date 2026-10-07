using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialParty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pty");

            migrationBuilder.CreateTable(
                name: "party",
                schema: "pty",
                columns: table => new
                {
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    party_number = table.Column<string>(type: "text", nullable: false),
                    party_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    preferred_language = table.Column<string>(type: "text", nullable: false),
                    birth_date_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    source_channel = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party", x => x.party_id);
                    table.CheckConstraint("ck_party_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_party_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_party_status", "status IN ('PROSPECT', 'ACTIVE', 'INACTIVE', 'DECEASED', 'DISSOLVED', 'MERGED', 'RESTRICTED', 'ANONYMISED')");
                    table.CheckConstraint("ck_party_type", "party_type IN ('PERSON', 'ORGANISATION')");
                });

            migrationBuilder.CreateTable(
                name: "intermediary",
                schema: "pty",
                columns: table => new
                {
                    intermediary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intermediary_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    register_name = table.Column<string>(type: "text", nullable: false),
                    chamber = table.Column<string>(type: "text", nullable: false),
                    register_number = table.Column<string>(type: "text", nullable: false),
                    registration_category = table.Column<string>(type: "text", nullable: false),
                    registration_date = table.Column<DateOnly>(type: "date", nullable: false),
                    register_status = table.Column<string>(type: "text", nullable: false),
                    verification_link = table.Column<string>(type: "text", nullable: true),
                    evidence_ref = table.Column<string>(type: "text", nullable: true),
                    register_verified_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intermediary", x => x.intermediary_id);
                    table.CheckConstraint("ck_intermediary_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_intermediary_register_status", "register_status IN ('ACTIVE', 'SUSPENDED', 'DELETED')");
                    table.CheckConstraint("ck_intermediary_status", "status IN ('ONBOARDING', 'ACTIVE', 'SUSPENDED', 'TERMINATED')");
                    table.ForeignKey(
                        name: "fk_intermediary_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_address",
                schema: "pty",
                columns: table => new
                {
                    address_id = table.Column<Guid>(type: "uuid", nullable: false),
                    types = table.Column<string[]>(type: "text[]", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    country = table.Column<string>(type: "char(2)", nullable: false),
                    street = table.Column<string>(type: "text", nullable: true),
                    number = table.Column<string>(type: "text", nullable: true),
                    building = table.Column<string>(type: "text", nullable: true),
                    floor = table.Column<string>(type: "text", nullable: true),
                    unit = table.Column<string>(type: "text", nullable: true),
                    postcode = table.Column<string>(type: "text", nullable: true),
                    locality = table.Column<string>(type: "text", nullable: true),
                    municipality = table.Column<string>(type: "text", nullable: true),
                    regional_unit = table.Column<string>(type: "text", nullable: true),
                    region = table.Column<string>(type: "text", nullable: true),
                    free_lines = table.Column<string[]>(type: "text[]", nullable: false),
                    latin_street = table.Column<string>(type: "text", nullable: true),
                    latin_building = table.Column<string>(type: "text", nullable: true),
                    latin_locality = table.Column<string>(type: "text", nullable: true),
                    latin_municipality = table.Column<string>(type: "text", nullable: true),
                    latin_regional_unit = table.Column<string>(type: "text", nullable: true),
                    latin_region = table.Column<string>(type: "text", nullable: true),
                    latin_free_lines = table.Column<string[]>(type: "text[]", nullable: false),
                    formatted_lines = table.Column<string[]>(type: "text[]", nullable: false),
                    formatted_lines_latin = table.Column<string[]>(type: "text[]", nullable: false),
                    validation_state = table.Column<string>(type: "text", nullable: false),
                    rule_set_id = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party_address", x => x.address_id);
                    table.CheckConstraint("ck_party_address_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_party_address_state", "validation_state IN ('VALIDATED', 'UNVALIDATED', 'INVALID')");
                    table.CheckConstraint("ck_party_address_types", "cardinality(types) >= 1");
                    table.CheckConstraint("ck_party_address_valid", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_party_address_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_contact_point",
                schema: "pty",
                columns: table => new
                {
                    contact_point_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    verification_status = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party_contact_point", x => x.contact_point_id);
                    table.CheckConstraint("ck_party_contact_point_purpose", "purpose IN ('PERSONAL', 'WORK')");
                    table.CheckConstraint("ck_party_contact_point_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_party_contact_point_type", "type IN ('MOBILE', 'LANDLINE', 'WORK_PHONE', 'FAX', 'EMAIL', 'SECURE_INBOX')");
                    table.CheckConstraint("ck_party_contact_point_valid", "valid_to IS NULL OR valid_to > valid_from");
                    table.CheckConstraint("ck_party_contact_point_verification", "verification_status IN ('UNVERIFIED', 'VERIFIED', 'BOUNCING')");
                    table.ForeignKey(
                        name: "fk_party_contact_point_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_identifier",
                schema: "pty",
                columns: table => new
                {
                    identifier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheme = table.Column<string>(type: "text", nullable: false),
                    value_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    value_blind_index = table.Column<string>(type: "text", nullable: false),
                    display_suffix = table.Column<string>(type: "text", nullable: false),
                    issuing_country = table.Column<string>(type: "char(2)", nullable: true),
                    verification_status = table.Column<string>(type: "text", nullable: false),
                    verification_source = table.Column<string>(type: "text", nullable: false),
                    verified_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    validator_version = table.Column<string>(type: "text", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party_identifier", x => x.identifier_id);
                    table.CheckConstraint("ck_party_identifier_blind_index", "value_blind_index ~ '^v[0-9]+:'");
                    table.CheckConstraint("ck_party_identifier_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_party_identifier_valid", "valid_to IS NULL OR valid_to > valid_from");
                    table.CheckConstraint("ck_party_identifier_verification", "verification_status IN ('SELF_DECLARED', 'DOCUMENT_VERIFIED', 'REGISTRY_VERIFIED', 'VERIFICATION_FAILED', 'EXPIRED')");
                    table.ForeignKey(
                        name: "fk_party_identifier_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_name",
                schema: "pty",
                columns: table => new
                {
                    name_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form = table.Column<string>(type: "text", nullable: false),
                    script = table.Column<string>(type: "text", nullable: false),
                    given_names = table.Column<string>(type: "text", nullable: true),
                    family_name = table.Column<string>(type: "text", nullable: true),
                    father_name = table.Column<string>(type: "text", nullable: true),
                    mother_name = table.Column<string>(type: "text", nullable: true),
                    organisation_name = table.Column<string>(type: "text", nullable: true),
                    trade_name = table.Column<string>(type: "text", nullable: true),
                    transliterator_version = table.Column<string>(type: "text", nullable: true),
                    source_document_ref = table.Column<string>(type: "text", nullable: true),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party_name", x => x.name_id);
                    table.CheckConstraint("ck_party_name_form", "form IN ('NATIVE', 'LATIN_GENERATED', 'LATIN_AS_ON_DOCUMENT')");
                    table.CheckConstraint("ck_party_name_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_party_name_valid", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_party_name_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_search_key",
                schema: "pty",
                columns: table => new
                {
                    key_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_kind = table.Column<string>(type: "text", nullable: false),
                    search_key = table.Column<string>(type: "text", nullable: false),
                    rule_version = table.Column<string>(type: "text", nullable: false),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party_search_key", x => x.key_id);
                    table.CheckConstraint("ck_party_search_key_kind", "key_kind IN ('FULL', 'PART')");
                    table.ForeignKey(
                        name: "fk_party_search_key_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "producer_code",
                schema: "pty",
                columns: table => new
                {
                    producer_code_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intermediary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    collect_premium = table.Column<bool>(type: "boolean", nullable: false),
                    issue_cover_notes = table.Column<bool>(type: "boolean", nullable: false),
                    bind_within_authority = table.Column<bool>(type: "boolean", nullable: false),
                    service_only = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_producer_code", x => x.producer_code_id);
                    table.CheckConstraint("ck_producer_code_status", "status IN ('ACTIVE', 'SUSPENDED', 'TERMINATED')");
                    table.CheckConstraint("ck_producer_code_validity", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_producer_code_intermediary",
                        column: x => x.intermediary_id,
                        principalSchema: "pty",
                        principalTable: "intermediary",
                        principalColumn: "intermediary_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_intermediary_party_id",
                schema: "pty",
                table: "intermediary",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "ux_intermediary_party",
                schema: "pty",
                table: "intermediary",
                columns: new[] { "legal_entity_id", "party_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_party_number",
                schema: "pty",
                table: "party",
                columns: new[] { "legal_entity_id", "party_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_party_address_party",
                schema: "pty",
                table: "party_address",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "ix_party_address_postcode",
                schema: "pty",
                table: "party_address",
                columns: new[] { "legal_entity_id", "postcode" });

            migrationBuilder.CreateIndex(
                name: "ix_party_contact_point_party",
                schema: "pty",
                table: "party_contact_point",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "ix_party_identifier_party",
                schema: "pty",
                table: "party_identifier",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "ux_party_identifier_current",
                schema: "pty",
                table: "party_identifier",
                columns: new[] { "legal_entity_id", "scheme", "value_blind_index" },
                unique: true,
                filter: "valid_to IS NULL AND recorded_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_party_name_party",
                schema: "pty",
                table: "party_name",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "ix_party_search_key_party",
                schema: "pty",
                table: "party_search_key",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "ix_party_search_key_prefix",
                schema: "pty",
                table: "party_search_key",
                columns: new[] { "legal_entity_id", "search_key" },
                filter: "recorded_to IS NULL")
                .Annotation("Npgsql:IndexOperators", new[] { "uuid_ops", "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_party_search_key_trgm",
                schema: "pty",
                table: "party_search_key",
                column: "search_key")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_producer_code_intermediary_id",
                schema: "pty",
                table: "producer_code",
                column: "intermediary_id");

            migrationBuilder.CreateIndex(
                name: "ux_producer_code_code",
                schema: "pty",
                table: "producer_code",
                columns: new[] { "legal_entity_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_address",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "party_contact_point",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "party_identifier",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "party_name",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "party_search_key",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "producer_code",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "intermediary",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "party",
                schema: "pty");
        }
    }
}
