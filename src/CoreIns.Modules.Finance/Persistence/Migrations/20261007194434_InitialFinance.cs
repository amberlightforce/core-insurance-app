using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFinance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fin");

            migrationBuilder.CreateTable(
                name: "book_profile",
                schema: "fin",
                columns: table => new
                {
                    legal_entity_code = table.Column<string>(type: "text", nullable: false),
                    functional_currency = table.Column<string>(type: "char(3)", nullable: false),
                    statutory_book = table.Column<string>(type: "text", nullable: false),
                    active_books = table.Column<string[]>(type: "text[]", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_book_profile", x => x.legal_entity_code);
                    table.CheckConstraint("ck_book_profile_currency", "functional_currency ~ '^[A-Z]{3}$'");
                });

            migrationBuilder.CreateTable(
                name: "business_event",
                schema: "fin",
                columns: table => new
                {
                    business_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legal_entity_code = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "text", nullable: false),
                    source_module = table.Column<string>(type: "text", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    registry_name = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<string>(type: "text", nullable: false),
                    aggregate_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<string>(type: "text", nullable: false),
                    aggregate_sequence = table.Column<long>(type: "bigint", nullable: false),
                    out_of_order = table.Column<bool>(type: "boolean", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_size = table.Column<int>(type: "integer", nullable: true),
                    set_index = table.Column<int>(type: "integer", nullable: true),
                    relevance = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    waiting_on = table.Column<string>(type: "text", nullable: true),
                    exception_reason = table.Column<string>(type: "text", nullable: true),
                    exception_detail = table.Column<string>(type: "text", nullable: true),
                    accounting_date = table.Column<DateOnly>(type: "date", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    business_keys = table.Column<string>(type: "jsonb", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_term_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    journal_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_business_event", x => x.business_event_id);
                    table.CheckConstraint("ck_business_event_posted", "status <> 'POSTED' OR cardinality(journal_ids) >= 1");
                    table.CheckConstraint("ck_business_event_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_business_event_relevance", "relevance IN ('POSTING', 'CONTEXT', 'IGNORED')");
                    table.CheckConstraint("ck_business_event_status", "status IN ('RECEIVED', 'WAITING', 'POSTED', 'NO_POSTING', 'SUSPENDED')");
                    table.CheckConstraint("ck_business_event_suspended", "(status = 'SUSPENDED') = (exception_reason IS NOT NULL)");
                    table.CheckConstraint("ck_business_event_waiting", "(status = 'WAITING') = (waiting_on IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "charge_type_view",
                schema: "fin",
                columns: table => new
                {
                    artefact_hash = table.Column<string>(type: "char(64)", nullable: false),
                    charge_type = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    gl_key = table.Column<string>(type: "text", nullable: false),
                    coverage = table.Column<string>(type: "text", nullable: true),
                    loaded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charge_type_view", x => new { x.artefact_hash, x.charge_type });
                    table.CheckConstraint("ck_charge_type_view_hash", "artefact_hash ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "event_catalogue",
                schema: "fin",
                columns: table => new
                {
                    registry_name = table.Column<string>(type: "text", nullable: false),
                    schema_major = table.Column<int>(type: "integer", nullable: false),
                    relevance = table.Column<string>(type: "text", nullable: false),
                    amount_fields = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_catalogue", x => new { x.registry_name, x.schema_major });
                    table.CheckConstraint("ck_event_catalogue_relevance", "relevance IN ('POSTING', 'CONTEXT', 'IGNORED')");
                });

            migrationBuilder.CreateTable(
                name: "financial_period",
                schema: "fin",
                columns: table => new
                {
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_period", x => x.period_id);
                    table.CheckConstraint("ck_financial_period_code", "period_code ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                    table.CheckConstraint("ck_financial_period_status", "status IN ('OPEN')");
                });

            migrationBuilder.CreateTable(
                name: "gl_account",
                schema: "fin",
                columns: table => new
                {
                    legal_entity_code = table.Column<string>(type: "text", nullable: false),
                    book = table.Column<string>(type: "text", nullable: false),
                    account_code = table.Column<string>(type: "text", nullable: false),
                    name_el = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    account_type = table.Column<string>(type: "text", nullable: false),
                    normal_balance = table.Column<string>(type: "text", nullable: false),
                    code_origin = table.Column<string>(type: "text", nullable: false),
                    system_only = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gl_account", x => new { x.legal_entity_code, x.book, x.account_code });
                    table.CheckConstraint("ck_gl_account_normal_balance", "normal_balance IN ('DEBIT', 'CREDIT')");
                    table.CheckConstraint("ck_gl_account_origin", "code_origin IN ('PRD09_ILLUSTRATIVE', 'TECHNICAL_PLACEHOLDER')");
                    table.CheckConstraint("ck_gl_account_status", "status IN ('ACTIVE', 'RETIRED')");
                    table.CheckConstraint("ck_gl_account_type", "account_type IN ('ASSET', 'LIABILITY', 'EQUITY', 'INCOME', 'EXPENSE', 'MEMO', 'CLEARING')");
                });

            migrationBuilder.CreateTable(
                name: "policy_context",
                schema: "fin",
                columns: table => new
                {
                    policy_term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_number = table.Column<string>(type: "text", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: false),
                    artefact_hash = table.Column<string>(type: "char(64)", nullable: false),
                    charge_types_loaded = table.Column<bool>(type: "boolean", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_sequence = table.Column<long>(type: "bigint", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_context", x => x.policy_term_id);
                    table.CheckConstraint("ck_policy_context_record_version", "record_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "posting_rule_set",
                schema: "fin",
                columns: table => new
                {
                    rule_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_code = table.Column<string>(type: "text", nullable: false),
                    book = table.Column<string>(type: "text", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posting_rule_set", x => x.rule_set_id);
                    table.CheckConstraint("ck_posting_rule_set_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_posting_rule_set_status", "status IN ('ACTIVE', 'SUPERSEDED')");
                    table.CheckConstraint("ck_posting_rule_set_version", "version_no >= 1");
                });

            migrationBuilder.CreateTable(
                name: "account_derivation",
                schema: "fin",
                columns: table => new
                {
                    legal_entity_code = table.Column<string>(type: "text", nullable: false),
                    book = table.Column<string>(type: "text", nullable: false),
                    gl_key = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    account_code = table.Column<string>(type: "text", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_derivation", x => new { x.legal_entity_code, x.book, x.gl_key, x.valid_from });
                    table.CheckConstraint("ck_account_derivation_period", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_account_derivation_account",
                        columns: x => new { x.legal_entity_code, x.book, x.account_code },
                        principalSchema: "fin",
                        principalTable: "gl_account",
                        principalColumns: new[] { "legal_entity_code", "book", "account_code" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_entry",
                schema: "fin",
                columns: table => new
                {
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_number = table.Column<string>(type: "text", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_code = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "text", nullable: false),
                    book = table.Column<string>(type: "text", nullable: false),
                    accounting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    source_module = table.Column<string>(type: "text", nullable: false),
                    source_event_type = table.Column<string>(type: "text", nullable: false),
                    source_event_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    source_ref = table.Column<string>(type: "text", nullable: false),
                    rule_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_set_version = table.Column<int>(type: "integer", nullable: false),
                    rule_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    reverses_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    functional_currency = table.Column<string>(type: "char(3)", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    posted_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    posted_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entry", x => x.journal_id);
                    table.CheckConstraint("ck_journal_entry_currency", "functional_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_journal_entry_reversal", "(source_type = 'REVERSAL') = (reverses_journal_id IS NOT NULL)");
                    table.CheckConstraint("ck_journal_entry_source_type", "source_type IN ('EVENT', 'REVERSAL')");
                    table.ForeignKey(
                        name: "fk_journal_entry_period",
                        column: x => x.period_id,
                        principalSchema: "fin",
                        principalTable: "financial_period",
                        principalColumn: "period_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journal_entry_reverses",
                        column: x => x.reverses_journal_id,
                        principalSchema: "fin",
                        principalTable: "journal_entry",
                        principalColumn: "journal_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journal_entry_rule_set",
                        column: x => x.rule_set_id,
                        principalSchema: "fin",
                        principalTable: "posting_rule_set",
                        principalColumn: "rule_set_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "posting_rule",
                schema: "fin",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_code = table.Column<string>(type: "text", nullable: false),
                    source_event = table.Column<string>(type: "text", nullable: false),
                    entry_type = table.Column<string>(type: "text", nullable: false),
                    source_account = table.Column<string>(type: "text", nullable: false),
                    charge_category = table.Column<string>(type: "text", nullable: true),
                    charge_type = table.Column<string>(type: "text", nullable: true),
                    specificity = table.Column<int>(type: "integer", nullable: false),
                    account_code = table.Column<string>(type: "text", nullable: true),
                    derive_from = table.Column<string>(type: "text", nullable: true),
                    description_el = table.Column<string>(type: "text", nullable: false),
                    description_en = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posting_rule", x => x.rule_id);
                    table.CheckConstraint("ck_posting_rule_derive", "derive_from IS NULL OR derive_from = 'GL_KEY'");
                    table.CheckConstraint("ck_posting_rule_specificity", "specificity = (CASE WHEN charge_category IS NULL THEN 0 ELSE 1 END) + (CASE WHEN charge_type IS NULL THEN 0 ELSE 1 END)");
                    table.CheckConstraint("ck_posting_rule_target", "(account_code IS NULL) <> (derive_from IS NULL)");
                    table.ForeignKey(
                        name: "fk_posting_rule_set",
                        column: x => x.rule_set_id,
                        principalSchema: "fin",
                        principalTable: "posting_rule_set",
                        principalColumn: "rule_set_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_line",
                schema: "fin",
                columns: table => new
                {
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    book = table.Column<string>(type: "text", nullable: false),
                    account_code = table.Column<string>(type: "text", nullable: false),
                    side = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    amount_functional = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    functional_currency = table.Column<string>(type: "char(3)", nullable: false),
                    rule_code = table.Column<string>(type: "text", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: true),
                    product_version = table.Column<string>(type: "text", nullable: true),
                    coverage_code = table.Column<string>(type: "text", nullable: true),
                    charge_type = table.Column<string>(type: "text", nullable: true),
                    charge_category = table.Column<string>(type: "text", nullable: true),
                    gl_key = table.Column<string>(type: "text", nullable: true),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_number = table.Column<string>(type: "text", nullable: true),
                    policy_term_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_line", x => x.line_id);
                    table.CheckConstraint("ck_journal_line_amount", "amount > 0 AND amount_functional > 0");
                    table.CheckConstraint("ck_journal_line_currency", "currency ~ '^[A-Z]{3}$' AND functional_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_journal_line_no", "line_no >= 1");
                    table.CheckConstraint("ck_journal_line_side", "side IN ('DEBIT', 'CREDIT')");
                    table.ForeignKey(
                        name: "fk_journal_line_entry",
                        column: x => x.journal_id,
                        principalSchema: "fin",
                        principalTable: "journal_entry",
                        principalColumn: "journal_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_derivation_legal_entity_code_book_account_code",
                schema: "fin",
                table: "account_derivation",
                columns: new[] { "legal_entity_code", "book", "account_code" });

            migrationBuilder.CreateIndex(
                name: "ix_business_event_aggregate",
                schema: "fin",
                table: "business_event",
                columns: new[] { "aggregate_type", "aggregate_id", "aggregate_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_business_event_status",
                schema: "fin",
                table: "business_event",
                columns: new[] { "legal_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_business_event_waiting",
                schema: "fin",
                table: "business_event",
                column: "waiting_on",
                filter: "waiting_on IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_business_event_source",
                schema: "fin",
                table: "business_event",
                column: "source_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_financial_period_code",
                schema: "fin",
                table: "financial_period",
                columns: new[] { "legal_entity_id", "period_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entry_date",
                schema: "fin",
                table: "journal_entry",
                columns: new[] { "legal_entity_id", "book", "accounting_date" });

            migrationBuilder.CreateIndex(
                name: "IX_journal_entry_period_id",
                schema: "fin",
                table: "journal_entry",
                column: "period_id");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entry_rule_set_id",
                schema: "fin",
                table: "journal_entry",
                column: "rule_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entry_source",
                schema: "fin",
                table: "journal_entry",
                columns: new[] { "legal_entity_id", "source_ref" });

            migrationBuilder.CreateIndex(
                name: "ux_journal_entry_number",
                schema: "fin",
                table: "journal_entry",
                columns: new[] { "legal_entity_id", "journal_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_journal_entry_reverses",
                schema: "fin",
                table: "journal_entry",
                column: "reverses_journal_id",
                unique: true,
                filter: "reverses_journal_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_journal_line_policy",
                schema: "fin",
                table: "journal_line",
                columns: new[] { "legal_entity_id", "policy_number" },
                filter: "policy_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_journal_line_no",
                schema: "fin",
                table: "journal_line",
                columns: new[] { "journal_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_policy_context_policy",
                schema: "fin",
                table: "policy_context",
                columns: new[] { "legal_entity_id", "policy_id" });

            migrationBuilder.CreateIndex(
                name: "ux_posting_rule_code",
                schema: "fin",
                table: "posting_rule",
                columns: new[] { "rule_set_id", "rule_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_posting_rule_set_version",
                schema: "fin",
                table: "posting_rule_set",
                columns: new[] { "legal_entity_code", "book", "version_no" },
                unique: true);

            // Deferred balance constraint, append-only triggers, rule-key index (REQ-FIN-068, -070, -049), then the
            // versioned GR-TEST reference data (chart, derivations, catalogue, rule set v1).
            migrationBuilder.Sql(FinanceSql.Objects);
            migrationBuilder.Sql(FinanceSeed.Sql(FinanceSeed.GrTestV1));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FinanceSql.DropObjects);

            migrationBuilder.DropTable(
                name: "account_derivation",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "book_profile",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "business_event",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "charge_type_view",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "event_catalogue",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "journal_line",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "policy_context",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "posting_rule",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "gl_account",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "journal_entry",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "financial_period",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "posting_rule_set",
                schema: "fin");
        }
    }
}
