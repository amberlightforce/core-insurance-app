using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pol");

            migrationBuilder.CreateTable(
                name: "job",
                schema: "pol",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    job_number = table.Column<string>(type: "text", nullable: false),
                    job_type = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    referred = table.Column<bool>(type: "boolean", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policyholder_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: false),
                    artefact_hash = table.Column<string>(type: "text", nullable: false),
                    rating_artefact_hash = table.Column<string>(type: "text", nullable: true),
                    resolution_hash = table.Column<string>(type: "text", nullable: false),
                    resolution_manifest = table.Column<string>(type: "jsonb", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    producer_code = table.Column<string>(type: "text", nullable: true),
                    quote_type = table.Column<string>(type: "text", nullable: false),
                    effective_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    expiration_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    current_version_no = table.Column<int>(type: "integer", nullable: false),
                    bound_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decline_id = table.Column<Guid>(type: "uuid", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job", x => x.job_id);
                    table.CheckConstraint("ck_job_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_job_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_job_period", "expiration_at > effective_at");
                    table.CheckConstraint("ck_job_quote_type", "quote_type IN ('QUICK', 'FULL')");
                    table.CheckConstraint("ck_job_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_job_state", "state IN ('DRAFT', 'QUOTED', 'BOUND', 'SCHEDULED', 'RESCINDED', 'WITHDRAWN', 'DECLINED', 'NOT_TAKEN', 'EXPIRED')");
                    table.CheckConstraint("ck_job_type", "job_type IN ('SUBMISSION')");
                });

            migrationBuilder.CreateTable(
                name: "policy",
                schema: "pol",
                columns: table => new
                {
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    policy_number = table.Column<string>(type: "text", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    policyholder_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy", x => x.policy_id);
                    table.CheckConstraint("ck_policy_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                });

            migrationBuilder.CreateTable(
                name: "quote_version",
                schema: "pol",
                columns: table => new
                {
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    draft_version = table.Column<int>(type: "integer", nullable: false),
                    risk_tree = table.Column<string>(type: "jsonb", nullable: false),
                    worksheet_id = table.Column<string>(type: "text", nullable: true),
                    worksheet_hash = table.Column<string>(type: "text", nullable: true),
                    bindable = table.Column<bool>(type: "boolean", nullable: true),
                    charges = table.Column<string>(type: "jsonb", nullable: true),
                    premium = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    taxes = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    total = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    issues = table.Column<string>(type: "jsonb", nullable: true),
                    uw_evaluation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    configuration_hash = table.Column<string>(type: "text", nullable: true),
                    quoted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    valid_until = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_version", x => x.quote_id);
                    table.CheckConstraint("ck_quote_version_draft", "draft_version >= 0");
                    table.CheckConstraint("ck_quote_version_no", "version_no >= 1 AND version_no <= 20");
                    table.CheckConstraint("ck_quote_version_quoted", "state <> 'QUOTED' OR (quoted_at IS NOT NULL AND charges IS NOT NULL AND total IS NOT NULL)");
                    table.CheckConstraint("ck_quote_version_state", "state IN ('DRAFT', 'QUOTED', 'SUPERSEDED', 'EXPIRED')");
                    table.ForeignKey(
                        name: "fk_quote_version_job",
                        column: x => x.job_id,
                        principalSchema: "pol",
                        principalTable: "job",
                        principalColumn: "job_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_term",
                schema: "pol",
                columns: table => new
                {
                    term_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_number = table.Column<int>(type: "integer", nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    valid_to = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: false),
                    artefact_hash = table.Column<string>(type: "text", nullable: false),
                    rating_artefact_hash = table.Column<string>(type: "text", nullable: true),
                    resolution_hash = table.Column<string>(type: "text", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    producer_code = table.Column<string>(type: "text", nullable: true),
                    payment_plan_ref = table.Column<string>(type: "text", nullable: false),
                    written_date = table.Column<DateOnly>(type: "date", nullable: false),
                    head_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_term", x => x.term_version_id);
                    table.CheckConstraint("ck_policy_term_number", "term_number >= 1");
                    table.CheckConstraint("ck_policy_term_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_policy_term_state", "state IN ('SCHEDULED', 'IN_FORCE', 'PENDING_CANCELLATION', 'CANCELLED', 'EXPIRED', 'VOIDED', 'REWRITTEN')");
                    table.CheckConstraint("ck_policy_term_valid", "valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_policy_term_policy",
                        column: x => x.policy_id,
                        principalSchema: "pol",
                        principalTable: "policy",
                        principalColumn: "policy_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_transaction",
                schema: "pol",
                columns: table => new
                {
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    effective_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    artefact_hash = table.Column<string>(type: "text", nullable: false),
                    rating_artefact_hash = table.Column<string>(type: "text", nullable: true),
                    resolution_hash = table.Column<string>(type: "text", nullable: false),
                    worksheet_id = table.Column<string>(type: "text", nullable: true),
                    intent = table.Column<string>(type: "jsonb", nullable: false),
                    premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    taxes = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_transaction", x => x.transaction_id);
                    table.CheckConstraint("ck_policy_transaction_kind", "kind IN ('ISSUANCE', 'CHANGE', 'CANCELLATION', 'REINSTATEMENT', 'REWRITE', 'RENEWAL', 'CARRY_FORWARD', 'SUSPENSION', 'REACTIVATION', 'VOID', 'REVERSAL')");
                    table.CheckConstraint("ck_policy_transaction_sequence", "sequence >= 1");
                    table.CheckConstraint("ck_policy_transaction_totals", "total = premium + taxes");
                    table.ForeignKey(
                        name: "fk_policy_transaction_policy",
                        column: x => x.policy_id,
                        principalSchema: "pol",
                        principalTable: "policy",
                        principalColumn: "policy_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "charge_line",
                schema: "pol",
                columns: table => new
                {
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    element_locator = table.Column<string>(type: "text", nullable: false),
                    coverage_code = table.Column<string>(type: "text", nullable: false),
                    charge_type = table.Column<string>(type: "text", nullable: false),
                    charge_category = table.Column<string>(type: "text", nullable: false),
                    delta_kind = table.Column<string>(type: "text", nullable: false),
                    annual_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: false),
                    booking_date = table.Column<DateOnly>(type: "date", nullable: false),
                    correlation_key = table.Column<string>(type: "text", nullable: false),
                    set_index = table.Column<int>(type: "integer", nullable: false),
                    set_size = table.Column<int>(type: "integer", nullable: false),
                    tax_treatment_ref = table.Column<string>(type: "text", nullable: true),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charge_line", x => x.charge_id);
                    table.CheckConstraint("ck_charge_line_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_charge_line_set", "set_index >= 1 AND set_index <= set_size");
                    table.CheckConstraint("ck_charge_line_valid", "valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_charge_line_transaction",
                        column: x => x.transaction_id,
                        principalSchema: "pol",
                        principalTable: "policy_transaction",
                        principalColumn: "transaction_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "segment",
                schema: "pol",
                columns: table => new
                {
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    valid_to = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    snapshot_hash = table.Column<string>(type: "text", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    worksheet_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_segment", x => x.segment_id);
                    table.CheckConstraint("ck_segment_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_segment_snapshot_hash", "snapshot_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_segment_valid", "valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_segment_transaction",
                        column: x => x.transaction_id,
                        principalSchema: "pol",
                        principalTable: "policy_transaction",
                        principalColumn: "transaction_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_charge_line_term",
                schema: "pol",
                table: "charge_line",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "ux_charge_line_set",
                schema: "pol",
                table: "charge_line",
                columns: new[] { "transaction_id", "set_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_job_policy",
                schema: "pol",
                table: "job",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_policyholder",
                schema: "pol",
                table: "job",
                columns: new[] { "legal_entity_id", "policyholder_party_id" });

            migrationBuilder.CreateIndex(
                name: "ux_job_number",
                schema: "pol",
                table: "job",
                columns: new[] { "legal_entity_id", "job_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_policy_number",
                schema: "pol",
                table: "policy",
                columns: new[] { "legal_entity_id", "policy_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_policy_term_current",
                schema: "pol",
                table: "policy_term",
                column: "term_id",
                unique: true,
                filter: "recorded_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_policy_term_number",
                schema: "pol",
                table: "policy_term",
                columns: new[] { "policy_id", "term_number" },
                unique: true,
                filter: "recorded_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_policy_transaction_job",
                schema: "pol",
                table: "policy_transaction",
                column: "job_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_policy_transaction_sequence",
                schema: "pol",
                table: "policy_transaction",
                columns: new[] { "policy_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_quote_version_job_no",
                schema: "pol",
                table: "quote_version",
                columns: new[] { "job_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_segment_term",
                schema: "pol",
                table: "segment",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "IX_segment_transaction_id",
                schema: "pol",
                table: "segment",
                column: "transaction_id");

            // Bitemporal non-overlap on PostgreSQL 17 (D-ARC-05, REQ-POL-079 translated from PG18 WITHOUT OVERLAPS): within
            // any record time, the versions of one policy's terms and of one term's segments never overlap in valid time.
            migrationBuilder.Sql("""
                ALTER TABLE pol.policy_term ADD CONSTRAINT ex_policy_term_no_overlap EXCLUDE USING gist (
                    policy_id WITH =,
                    tstzrange(valid_from, valid_to, '[)') WITH &&,
                    tstzrange(recorded_from, recorded_to, '[)') WITH &&);
                ALTER TABLE pol.segment ADD CONSTRAINT ex_segment_no_overlap EXCLUDE USING gist (
                    term_id WITH =,
                    tstzrange(valid_from, valid_to, '[)') WITH &&,
                    tstzrange(recorded_from, recorded_to, '[)') WITH &&);
                """);

            // Append-only transaction log and charge deltas (REQ-POL-075): the app role has no UPDATE/DELETE grant, and the
            // trigger refuses them for every role. Segments and term versions are immutable except that a current row's
            // record period may be closed once (supersession, REQ-POL-078).
            migrationBuilder.Sql("""
                CREATE FUNCTION pol.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'pol.% is append-only', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                END $$;
                CREATE TRIGGER tr_policy_transaction_append_only BEFORE UPDATE OR DELETE ON pol.policy_transaction
                    FOR EACH ROW EXECUTE FUNCTION pol.reject_change();
                CREATE TRIGGER tr_charge_line_append_only BEFORE UPDATE OR DELETE ON pol.charge_line
                    FOR EACH ROW EXECUTE FUNCTION pol.reject_change();

                CREATE FUNCTION pol.only_close_record_period() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'pol.% rows are never deleted', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.recorded_to IS NOT NULL OR NEW.recorded_to IS NULL
                       OR (to_jsonb(NEW) - 'recorded_to') IS DISTINCT FROM (to_jsonb(OLD) - 'recorded_to') THEN
                        RAISE EXCEPTION 'pol.% is immutable except closing its record period', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER tr_segment_immutable BEFORE UPDATE OR DELETE ON pol.segment
                    FOR EACH ROW EXECUTE FUNCTION pol.only_close_record_period();
                CREATE TRIGGER tr_policy_term_immutable BEFORE UPDATE OR DELETE ON pol.policy_term
                    FOR EACH ROW EXECUTE FUNCTION pol.only_close_record_period();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_policy_transaction_append_only ON pol.policy_transaction;
                DROP TRIGGER IF EXISTS tr_charge_line_append_only ON pol.charge_line;
                DROP TRIGGER IF EXISTS tr_segment_immutable ON pol.segment;
                DROP TRIGGER IF EXISTS tr_policy_term_immutable ON pol.policy_term;
                DROP FUNCTION IF EXISTS pol.reject_change();
                DROP FUNCTION IF EXISTS pol.only_close_record_period();
                """);

            migrationBuilder.DropTable(
                name: "charge_line",
                schema: "pol");

            migrationBuilder.DropTable(
                name: "policy_term",
                schema: "pol");

            migrationBuilder.DropTable(
                name: "quote_version",
                schema: "pol");

            migrationBuilder.DropTable(
                name: "segment",
                schema: "pol");

            migrationBuilder.DropTable(
                name: "job",
                schema: "pol");

            migrationBuilder.DropTable(
                name: "policy_transaction",
                schema: "pol");

            migrationBuilder.DropTable(
                name: "policy",
                schema: "pol");
        }
    }
}
