using System;
using CoreIns.Modules.Reinsurance.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialReinsurance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ri");

            migrationBuilder.CreateTable(
                name: "contract",
                schema: "ri",
                columns: table => new
                {
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    contract_number = table.Column<string>(type: "text", nullable: false),
                    stable_treaty_id = table.Column<string>(type: "text", nullable: false),
                    contract_type = table.Column<string>(type: "text", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    participants = table.Column<string[]>(type: "text[]", nullable: false),
                    submitted_by = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    return_reason = table.Column<string>(type: "text", nullable: true),
                    activated_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    expired_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contract", x => x.contract_id);
                    table.CheckConstraint("ck_contract_activated_shape", "status NOT IN ('ACTIVE', 'EXPIRED') OR activated_at IS NOT NULL");
                    table.CheckConstraint("ck_contract_currency", "currency = 'EUR'");
                    table.CheckConstraint("ck_contract_decided_shape", "status NOT IN ('APPROVED', 'ACTIVE', 'EXPIRED') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL)");
                    table.CheckConstraint("ck_contract_expired_shape", "status <> 'EXPIRED' OR expired_at IS NOT NULL");
                    table.CheckConstraint("ck_contract_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_contract_participants", "cardinality(participants) >= 1");
                    table.CheckConstraint("ck_contract_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_contract_status", "status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'EXPIRED', 'CLOSED')");
                    table.CheckConstraint("ck_contract_submitted_shape", "status NOT IN ('PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'EXPIRED') OR (submitted_by IS NOT NULL AND approval_request_id IS NOT NULL)");
                    table.CheckConstraint("ck_contract_type", "contract_type IN ('XOL_PER_RISK')");
                    table.CheckConstraint("ck_contract_year", "contract_year BETWEEN 1990 AND 2200");
                });

            migrationBuilder.CreateTable(
                name: "contract_version",
                schema: "ri",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: false),
                    known_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    known_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    content_rev = table.Column<int>(type: "integer", nullable: false),
                    placed_pct = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contract_version", x => x.version_id);
                    table.CheckConstraint("ck_contract_version_approved", "(approved_at IS NULL) = (approved_by IS NULL)");
                    table.CheckConstraint("ck_contract_version_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_contract_version_known", "known_to IS NULL OR known_to > known_from");
                    table.CheckConstraint("ck_contract_version_no", "version_no >= 1");
                    table.CheckConstraint("ck_contract_version_period", "valid_to > valid_from");
                    table.CheckConstraint("ck_contract_version_placed", "placed_pct > 0 AND placed_pct <= 100");
                    table.CheckConstraint("ck_contract_version_rev", "content_rev >= 1");
                    table.ForeignKey(
                        name: "fk_contract_version_contract",
                        column: x => x.contract_id,
                        principalSchema: "ri",
                        principalTable: "contract",
                        principalColumn: "contract_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "clause",
                schema: "ri",
                columns: table => new
                {
                    clause_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    alae_included = table.Column<bool>(type: "boolean", nullable: false),
                    statutory_interest_included = table.Column<bool>(type: "boolean", nullable: false),
                    recoveries_inure = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clause", x => x.clause_id);
                    table.CheckConstraint("ck_clause_inure", "recoveries_inure IN ('REALISED_ONLY')");
                    table.CheckConstraint("ck_clause_rev", "rev >= 1");
                    table.ForeignKey(
                        name: "fk_clause_version",
                        column: x => x.version_id,
                        principalSchema: "ri",
                        principalTable: "contract_version",
                        principalColumn: "version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "participation",
                schema: "ri",
                columns: table => new
                {
                    participation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    reinsurer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    signed_line_pct = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    lead = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_participation", x => x.participation_id);
                    table.CheckConstraint("ck_participation_line", "signed_line_pct > 0 AND signed_line_pct <= 100");
                    table.CheckConstraint("ck_participation_rev", "rev >= 1");
                    table.ForeignKey(
                        name: "fk_participation_version",
                        column: x => x.version_id,
                        principalSchema: "ri",
                        principalTable: "contract_version",
                        principalColumn: "version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "section",
                schema: "ri",
                columns: table => new
                {
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    section_no = table.Column<int>(type: "integer", nullable: false),
                    product_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    coverage_codes = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_section", x => x.section_id);
                    table.CheckConstraint("ck_section_rev", "rev >= 1 AND section_no >= 1");
                    table.CheckConstraint("ck_section_scope", "cardinality(product_codes) >= 1 AND cardinality(coverage_codes) >= 1");
                    table.ForeignKey(
                        name: "fk_section_version",
                        column: x => x.version_id,
                        principalSchema: "ri",
                        principalTable: "contract_version",
                        principalColumn: "version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "layer",
                schema: "ri",
                columns: table => new
                {
                    layer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rev = table.Column<int>(type: "integer", nullable: false),
                    layer_no = table.Column<int>(type: "integer", nullable: false),
                    attachment = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    limit_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    aad = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    aal = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layer", x => x.layer_id);
                    table.CheckConstraint("ck_layer_amounts", "attachment >= 0 AND limit_amount > 0 AND aad >= 0 AND (aal IS NULL OR aal > 0)");
                    table.CheckConstraint("ck_layer_currency", "currency = 'EUR'");
                    table.CheckConstraint("ck_layer_no", "layer_no >= 1 AND rev >= 1");
                    table.ForeignKey(
                        name: "fk_layer_section",
                        column: x => x.section_id,
                        principalSchema: "ri",
                        principalTable: "section",
                        principalColumn: "section_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_layer_version",
                        column: x => x.version_id,
                        principalSchema: "ri",
                        principalTable: "contract_version",
                        principalColumn: "version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_clause_version_rev",
                schema: "ri",
                table: "clause",
                columns: new[] { "version_id", "rev" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_contract_status",
                schema: "ri",
                table: "contract",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_contract_number",
                schema: "ri",
                table: "contract",
                columns: new[] { "legal_entity_id", "contract_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_contract_treaty_year",
                schema: "ri",
                table: "contract",
                columns: new[] { "legal_entity_id", "stable_treaty_id", "contract_year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_contract_version_no",
                schema: "ri",
                table: "contract_version",
                columns: new[] { "contract_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_layer_version_rev",
                schema: "ri",
                table: "layer",
                columns: new[] { "version_id", "rev" });

            migrationBuilder.CreateIndex(
                name: "ux_layer_no",
                schema: "ri",
                table: "layer",
                columns: new[] { "section_id", "layer_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_participation_reinsurer",
                schema: "ri",
                table: "participation",
                column: "reinsurer_party_id");

            migrationBuilder.CreateIndex(
                name: "ux_participation_one_lead",
                schema: "ri",
                table: "participation",
                columns: new[] { "version_id", "rev" },
                unique: true,
                filter: "lead");

            migrationBuilder.CreateIndex(
                name: "ux_participation_reinsurer",
                schema: "ri",
                table: "participation",
                columns: new[] { "version_id", "rev", "reinsurer_party_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_section_no",
                schema: "ri",
                table: "section",
                columns: new[] { "version_id", "rev", "section_no" },
                unique: true);

            // The exclusion constraint on the valid period and the integrity triggers (REQ-RI-057, -065; PITFALLS 8, 17, 40).
            migrationBuilder.Sql(ReinsuranceDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReinsuranceDatabaseSql.Down);

            migrationBuilder.DropTable(
                name: "clause",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "layer",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "participation",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "section",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "contract_version",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "contract",
                schema: "ri");
        }
    }
}
