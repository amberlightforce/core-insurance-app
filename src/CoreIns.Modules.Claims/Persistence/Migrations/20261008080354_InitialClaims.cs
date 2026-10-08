using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "clm");

            migrationBuilder.CreateTable(
                name: "claim",
                schema: "clm",
                columns: table => new
                {
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_number = table.Column<string>(type: "text", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_number = table.Column<string>(type: "text", nullable: false),
                    insured_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_ref = table.Column<string>(type: "text", nullable: false),
                    snapshot_segment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    snapshot_valid_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    snapshot_known_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    snapshot_status = table.Column<string>(type: "text", nullable: false),
                    policy_in_force_at_loss = table.Column<bool>(type: "boolean", nullable: false),
                    policy_status_at_loss = table.Column<string>(type: "text", nullable: true),
                    snapshot_coverage_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: true),
                    line_of_business = table.Column<string>(type: "text", nullable: false),
                    loss_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    loss_date = table.Column<DateOnly>(type: "date", nullable: false),
                    notice_on = table.Column<DateOnly>(type: "date", nullable: false),
                    loss_cause = table.Column<string>(type: "text", nullable: false),
                    loss_location_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    description_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    receipt_medium = table.Column<string>(type: "text", nullable: true),
                    handling_segment = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    sub_status = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    close_reason_code = table.Column<string>(type: "text", nullable: true),
                    coverage_in_question = table.Column<bool>(type: "boolean", nullable: false),
                    duplicate_of_claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    duplicate_reason_code = table.Column<string>(type: "text", nullable: true),
                    handler = table.Column<string>(type: "text", nullable: true),
                    reopen_count = table.Column<int>(type: "integer", nullable: false),
                    last_exposure_sequence = table.Column<int>(type: "integer", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim", x => x.claim_id);
                    table.CheckConstraint("ck_claim_closed_shape", "status <> 'CLOSED' OR (sub_status IS NULL AND outcome IS NOT NULL AND closed_at IS NOT NULL)");
                    table.CheckConstraint("ck_claim_exposure_sequence", "last_exposure_sequence >= 0");
                    table.CheckConstraint("ck_claim_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_claim_notice_after_loss", "notice_on >= loss_date");
                    table.CheckConstraint("ck_claim_open_shape", "status <> 'OPEN' OR (sub_status IS NOT NULL AND outcome IS NULL AND closed_at IS NULL)");
                    table.CheckConstraint("ck_claim_outcome", "outcome IS NULL OR outcome IN ('COMPLETED', 'DENIED', 'WITHDRAWN', 'DUPLICATE', 'NO_PAYMENT')");
                    table.CheckConstraint("ck_claim_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_claim_snapshot_status", "snapshot_status IN ('PENDING', 'VERIFIED', 'REVERIFICATION_REQUIRED')");
                    table.CheckConstraint("ck_claim_status", "status IN ('DRAFT', 'OPEN', 'CLOSED')");
                    table.CheckConstraint("ck_claim_sub_status", "sub_status IS NULL OR sub_status IN ('NEW', 'IN_PROGRESS', 'UNDER_INVESTIGATION', 'SETTLED')");
                    table.ForeignKey(
                        name: "fk_claim_duplicate_of",
                        column: x => x.duplicate_of_claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "claimant",
                schema: "clm",
                columns: table => new
                {
                    claimant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimant_type = table.Column<string>(type: "text", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claimant", x => x.claimant_id);
                    table.CheckConstraint("ck_claimant_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_claimant_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_claimant_type", "claimant_type IN ('INSURED', 'THIRD_PARTY', 'GUARANTEE_FUND', 'BUREAU')");
                    table.ForeignKey(
                        name: "fk_claimant_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fnol_snapshot",
                schema: "clm",
                columns: table => new
                {
                    fnol_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    reporter_party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fnol_snapshot", x => x.fnol_id);
                    table.CheckConstraint("ck_fnol_snapshot_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_fnol_snapshot_record_version", "record_version >= 1");
                    table.ForeignKey(
                        name: "fk_fnol_snapshot_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident",
                schema: "clm",
                columns: table => new
                {
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_type = table.Column<string>(type: "text", nullable: false),
                    vehicle_ref = table.Column<string>(type: "text", nullable: true),
                    drivable = table.Column<bool>(type: "boolean", nullable: true),
                    damage_areas = table.Column<string[]>(type: "text[]", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident", x => x.incident_id);
                    table.CheckConstraint("ck_incident_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_incident_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_incident_type", "incident_type IN ('VEHICLE', 'PROPERTY', 'INJURY', 'LIABILITY')");
                    table.ForeignKey(
                        name: "fk_incident_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exposure",
                schema: "clm",
                columns: table => new
                {
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exposure_number = table.Column<string>(type: "text", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    coverage_code = table.Column<string>(type: "text", nullable: false),
                    claimant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    sub_status = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    coverage_indication = table.Column<string>(type: "text", nullable: false),
                    coverage_decision = table.Column<string>(type: "text", nullable: false),
                    duplicate_reason = table.Column<string>(type: "text", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exposure", x => x.exposure_id);
                    table.CheckConstraint("ck_exposure_closed_shape", "status <> 'CLOSED' OR (sub_status IS NULL AND outcome IS NOT NULL AND closed_at IS NOT NULL)");
                    table.CheckConstraint("ck_exposure_decision", "coverage_decision IN ('PENDING', 'COVERED', 'COVERED_WITH_RESERVATION', 'PARTIALLY_COVERED', 'NOT_COVERED')");
                    table.CheckConstraint("ck_exposure_indication", "coverage_indication IN ('COVERED', 'NOT_COVERED', 'IN_QUESTION')");
                    table.CheckConstraint("ck_exposure_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_exposure_open_shape", "status <> 'OPEN' OR (sub_status IS NOT NULL AND outcome IS NULL AND closed_at IS NULL)");
                    table.CheckConstraint("ck_exposure_outcome", "outcome IS NULL OR outcome IN ('COMPLETED', 'DENIED', 'WITHDRAWN', 'DUPLICATE', 'NO_PAYMENT')");
                    table.CheckConstraint("ck_exposure_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_exposure_sequence", "sequence >= 1");
                    table.CheckConstraint("ck_exposure_status", "status IN ('OPEN', 'CLOSED')");
                    table.CheckConstraint("ck_exposure_sub_status", "sub_status IS NULL OR sub_status IN ('NEW', 'IN_PROGRESS')");
                    table.ForeignKey(
                        name: "fk_exposure_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_exposure_claimant",
                        column: x => x.claimant_id,
                        principalSchema: "clm",
                        principalTable: "claimant",
                        principalColumn: "claimant_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_exposure_incident",
                        column: x => x.incident_id,
                        principalSchema: "clm",
                        principalTable: "incident",
                        principalColumn: "incident_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_claim_duplicate_of_claim_id",
                schema: "clm",
                table: "claim",
                column: "duplicate_of_claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_insured_party",
                schema: "clm",
                table: "claim",
                columns: new[] { "legal_entity_id", "insured_party_id" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_policy_loss_date",
                schema: "clm",
                table: "claim",
                columns: new[] { "legal_entity_id", "policy_id", "loss_date" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_policy_number",
                schema: "clm",
                table: "claim",
                columns: new[] { "legal_entity_id", "policy_number" });

            migrationBuilder.CreateIndex(
                name: "ux_claim_number",
                schema: "clm",
                table: "claim",
                columns: new[] { "legal_entity_id", "claim_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_claimant_party",
                schema: "clm",
                table: "claimant",
                columns: new[] { "legal_entity_id", "party_id" });

            migrationBuilder.CreateIndex(
                name: "ux_claimant_claim_party",
                schema: "clm",
                table: "claimant",
                columns: new[] { "claim_id", "party_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exposure_claimant_id",
                schema: "clm",
                table: "exposure",
                column: "claimant_id");

            migrationBuilder.CreateIndex(
                name: "IX_exposure_incident_id",
                schema: "clm",
                table: "exposure",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ux_exposure_claim_sequence",
                schema: "clm",
                table: "exposure",
                columns: new[] { "claim_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_exposure_number",
                schema: "clm",
                table: "exposure",
                columns: new[] { "legal_entity_id", "exposure_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_exposure_open_coverage_claimant_incident",
                schema: "clm",
                table: "exposure",
                columns: new[] { "claim_id", "coverage_code", "claimant_id", "incident_id" },
                unique: true,
                filter: "status = 'OPEN' AND duplicate_reason IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_fnol_snapshot_claim",
                schema: "clm",
                table: "fnol_snapshot",
                column: "claim_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_claim",
                schema: "clm",
                table: "incident",
                column: "claim_id");

            // REQ-CLM-044: the FNOL snapshot is immutable (the app role has no UPDATE/DELETE grant; the trigger refuses them
            // for every role). PRD-07 §7.1: claim number, legal entity, policy reference, loss instant and creation facts of a
            // claim never change (re-verification adoption of a new snapshot is a later, audited path and keeps the policy).
            migrationBuilder.Sql("""
                CREATE FUNCTION clm.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'clm.% is immutable', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                END $$;
                CREATE TRIGGER tr_fnol_snapshot_immutable BEFORE UPDATE OR DELETE ON clm.fnol_snapshot
                    FOR EACH ROW EXECUTE FUNCTION clm.reject_change();
                CREATE TRIGGER tr_fnol_snapshot_no_truncate BEFORE TRUNCATE ON clm.fnol_snapshot
                    FOR EACH STATEMENT EXECUTE FUNCTION clm.reject_change();

                CREATE FUNCTION clm.claim_identity_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'clm.claim rows are never deleted' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.claim_id <> OLD.claim_id OR NEW.claim_number <> OLD.claim_number OR NEW.legal_entity_id <> OLD.legal_entity_id
                       OR NEW.jurisdiction <> OLD.jurisdiction OR NEW.policy_id <> OLD.policy_id OR NEW.policy_number <> OLD.policy_number
                       OR NEW.loss_at <> OLD.loss_at OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
                        RAISE EXCEPTION 'clm.claim identity columns are immutable' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER tr_claim_identity_frozen BEFORE UPDATE OR DELETE ON clm.claim
                    FOR EACH ROW EXECUTE FUNCTION clm.claim_identity_frozen();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_fnol_snapshot_immutable ON clm.fnol_snapshot;
                DROP TRIGGER IF EXISTS tr_fnol_snapshot_no_truncate ON clm.fnol_snapshot;
                DROP TRIGGER IF EXISTS tr_claim_identity_frozen ON clm.claim;
                DROP FUNCTION IF EXISTS clm.reject_change();
                DROP FUNCTION IF EXISTS clm.claim_identity_frozen();
                """);

            migrationBuilder.DropTable(
                name: "exposure",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "fnol_snapshot",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "claimant",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "incident",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "claim",
                schema: "clm");
        }
    }
}
