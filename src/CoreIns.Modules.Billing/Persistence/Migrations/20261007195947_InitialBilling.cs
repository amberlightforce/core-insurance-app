using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "bil");

            migrationBuilder.CreateTable(
                name: "billing_account",
                schema: "bil",
                columns: table => new
                {
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    account_number = table.Column<string>(type: "text", nullable: false),
                    payer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_account", x => x.billing_account_id);
                    table.CheckConstraint("ck_billing_account_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_billing_account_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_billing_account_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_billing_account_status", "status IN ('ACTIVE', 'SUSPENDED', 'CLOSING', 'CLOSED')");
                });

            migrationBuilder.CreateTable(
                name: "charge",
                schema: "bil",
                columns: table => new
                {
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_size = table.Column<int>(type: "integer", nullable: false),
                    set_index = table.Column<int>(type: "integer", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    element_locator = table.Column<string>(type: "text", nullable: false),
                    coverage_code = table.Column<string>(type: "text", nullable: false),
                    charge_type = table.Column<string>(type: "text", nullable: false),
                    charge_category = table.Column<string>(type: "text", nullable: false),
                    delta_kind = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    booking_date = table.Column<DateOnly>(type: "date", nullable: false),
                    correlation_key = table.Column<string>(type: "text", nullable: false),
                    tax_treatment_ref = table.Column<string>(type: "text", nullable: true),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_sequence = table.Column<long>(type: "bigint", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    quarantine_reason = table.Column<string>(type: "text", nullable: true),
                    fiscal_category_key = table.Column<string>(type: "text", nullable: true),
                    written_entry_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charge", x => x.charge_id);
                    table.CheckConstraint("ck_charge_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_charge_quarantine", "(status = 'QUARANTINED') = (quarantine_reason IS NOT NULL)");
                    table.CheckConstraint("ck_charge_set", "set_index >= 1 AND set_index <= set_size");
                    table.CheckConstraint("ck_charge_status", "status IN ('RECEIVED', 'WRITTEN', 'ACCRUED', 'SCHEDULED', 'QUARANTINED')");
                    table.CheckConstraint("ck_charge_valid", "valid_to IS NULL OR valid_to > valid_from");
                });

            migrationBuilder.CreateTable(
                name: "intake_exception",
                schema: "bil",
                columns: table => new
                {
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    raised_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_exception", x => x.exception_id);
                });

            migrationBuilder.CreateTable(
                name: "ledger_account",
                schema: "bil",
                columns: table => new
                {
                    account_code = table.Column<string>(type: "text", nullable: false),
                    name_en = table.Column<string>(type: "text", nullable: false),
                    name_el = table.Column<string>(type: "text", nullable: false),
                    account_type = table.Column<string>(type: "text", nullable: false),
                    normal_balance = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_account", x => x.account_code);
                    table.CheckConstraint("ck_ledger_account_code", "account_code ~ '^LA-[0-9]{2,3}$'");
                    table.CheckConstraint("ck_ledger_account_normal", "normal_balance IN ('DEBIT', 'CREDIT', 'EITHER')");
                });

            migrationBuilder.CreateTable(
                name: "ledger_entry",
                schema: "bil",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_type = table.Column<string>(type: "text", nullable: false),
                    accounting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    cause_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cause_operation = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    lineage_keys = table.Column<string>(type: "jsonb", nullable: false),
                    reverses_entry_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entry", x => x.entry_id);
                    table.CheckConstraint("ck_ledger_entry_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                });

            migrationBuilder.CreateTable(
                name: "invoice",
                schema: "bil",
                columns: table => new
                {
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_number = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    total = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    fiscal_status = table.Column<string>(type: "text", nullable: false),
                    fiscal_trigger_point = table.Column<string>(type: "text", nullable: true),
                    fiscal_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fiscal_document_type = table.Column<string>(type: "text", nullable: true),
                    fiscal_series = table.Column<string>(type: "text", nullable: true),
                    fiscal_number = table.Column<string>(type: "text", nullable: true),
                    fiscal_mark = table.Column<string>(type: "text", nullable: true),
                    fiscal_uid = table.Column<string>(type: "text", nullable: true),
                    fiscal_rejection_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice", x => x.invoice_id);
                    table.CheckConstraint("ck_invoice_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_invoice_fiscal_status", "fiscal_status IN ('NOT_REQUESTED', 'PENDING', 'REGISTERED', 'REJECTED', 'REQUEST_FAILED')");
                    table.CheckConstraint("ck_invoice_kind", "kind IN ('INVOICE', 'CREDIT_NOTE')");
                    table.CheckConstraint("ck_invoice_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_invoice_state", "state IN ('PLANNED', 'BILLED', 'DUE', 'PAID', 'PARTIALLY_PAID', 'OVERDUE', 'WRITTEN_OFF', 'REVERSED')");
                    table.CheckConstraint("ck_invoice_total", "total > 0");
                    table.ForeignKey(
                        name: "fk_invoice_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_instance",
                schema: "bil",
                columns: table => new
                {
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_number = table.Column<string>(type: "text", nullable: false),
                    term_number = table.Column<int>(type: "integer", nullable: false),
                    bound_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: false),
                    artefact_hash = table.Column<string>(type: "text", nullable: false),
                    plan_code = table.Column<string>(type: "text", nullable: false),
                    bill_mode = table.Column<string>(type: "text", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    term_from = table.Column<DateOnly>(type: "date", nullable: false),
                    term_to = table.Column<DateOnly>(type: "date", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_sequence = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_instance", x => x.term_id);
                    table.CheckConstraint("ck_plan_instance_bill_mode", "bill_mode IN ('DIRECT_BILL', 'AGENCY_BILL')");
                    table.CheckConstraint("ck_plan_instance_term", "term_to > term_from");
                    table.ForeignKey(
                        name: "fk_plan_instance_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt",
                schema: "bil",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_number = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: false),
                    accounting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    invoice_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_reference = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    suspense_reason = table.Column<string>(type: "text", nullable: true),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt", x => x.receipt_id);
                    table.CheckConstraint("ck_receipt_amount", "amount > 0");
                    table.CheckConstraint("ck_receipt_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_receipt_method", "method IN ('BANK_TRANSFER', 'CASHIER')");
                    table.CheckConstraint("ck_receipt_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_receipt_state", "state IN ('RECEIVED', 'ALLOCATED', 'PARTIALLY_ALLOCATED', 'SUSPENSE', 'REVERSED', 'REFUNDED')");
                    table.CheckConstraint("ck_receipt_suspense", "(state = 'SUSPENSE') = (suspense_reason IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_receipt_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_rule",
                schema: "bil",
                columns: table => new
                {
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    charge_category = table.Column<string>(type: "text", nullable: false),
                    bill_mode = table.Column<string>(type: "text", nullable: false),
                    jurisdiction = table.Column<string>(type: "text", nullable: false),
                    qualifier = table.Column<string>(type: "text", nullable: false),
                    debit_account = table.Column<string>(type: "text", nullable: false),
                    credit_account = table.Column<string>(type: "text", nullable: false),
                    amount_expression = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_rule", x => new { x.rule_id, x.version });
                    table.CheckConstraint("ck_ledger_rule_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_ledger_rule_credit",
                        column: x => x.credit_account,
                        principalSchema: "bil",
                        principalTable: "ledger_account",
                        principalColumn: "account_code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_rule_debit",
                        column: x => x.debit_account,
                        principalSchema: "bil",
                        principalTable: "ledger_account",
                        principalColumn: "account_code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_line",
                schema: "bil",
                columns: table => new
                {
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    account_code = table.Column<string>(type: "text", nullable: false),
                    side = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    term_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: true),
                    charge_type = table.Column<string>(type: "text", nullable: true),
                    charge_category = table.Column<string>(type: "text", nullable: true),
                    coverage_code = table.Column<string>(type: "text", nullable: true),
                    product_code = table.Column<string>(type: "text", nullable: true),
                    bill_mode = table.Column<string>(type: "text", nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invoice_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    allocation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_line", x => x.line_id);
                    table.CheckConstraint("ck_ledger_line_amount", "amount > 0");
                    table.CheckConstraint("ck_ledger_line_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_ledger_line_side", "side IN ('DEBIT', 'CREDIT')");
                    table.ForeignKey(
                        name: "fk_ledger_line_account",
                        column: x => x.account_code,
                        principalSchema: "bil",
                        principalTable: "ledger_account",
                        principalColumn: "account_code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_line_entry",
                        column: x => x.entry_id,
                        principalSchema: "bil",
                        principalTable: "ledger_entry",
                        principalColumn: "entry_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_item",
                schema: "bil",
                columns: table => new
                {
                    invoice_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    element_locator = table.Column<string>(type: "text", nullable: false),
                    coverage_code = table.Column<string>(type: "text", nullable: false),
                    charge_type = table.Column<string>(type: "text", nullable: false),
                    charge_category = table.Column<string>(type: "text", nullable: false),
                    fiscal_category_key = table.Column<string>(type: "text", nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_item", x => x.invoice_item_id);
                    table.CheckConstraint("ck_invoice_item_amount", "amount > 0");
                    table.CheckConstraint("ck_invoice_item_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_invoice_item_state", "state IN ('PLANNED', 'BILLED', 'OPEN', 'SETTLED', 'CANCELLED', 'WRITTEN_OFF')");
                    table.ForeignKey(
                        name: "fk_invoice_item_charge",
                        column: x => x.charge_id,
                        principalSchema: "bil",
                        principalTable: "charge",
                        principalColumn: "charge_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_item_invoice",
                        column: x => x.invoice_id,
                        principalSchema: "bil",
                        principalTable: "invoice",
                        principalColumn: "invoice_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "allocation",
                schema: "bil",
                columns: table => new
                {
                    allocation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_allocation", x => x.allocation_id);
                    table.CheckConstraint("ck_allocation_amount", "amount > 0");
                    table.CheckConstraint("ck_allocation_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_allocation_source", "source IN ('RULE', 'AI', 'MANUAL')");
                    table.ForeignKey(
                        name: "fk_allocation_item",
                        column: x => x.invoice_item_id,
                        principalSchema: "bil",
                        principalTable: "invoice_item",
                        principalColumn: "invoice_item_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_allocation_receipt",
                        column: x => x.receipt_id,
                        principalSchema: "bil",
                        principalTable: "receipt",
                        principalColumn: "receipt_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_allocation_item",
                schema: "bil",
                table: "allocation",
                column: "invoice_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_allocation_receipt",
                schema: "bil",
                table: "allocation",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "ux_billing_account_active_payer",
                schema: "bil",
                table: "billing_account",
                columns: new[] { "legal_entity_id", "payer_party_id", "currency" },
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "ux_billing_account_number",
                schema: "bil",
                table: "billing_account",
                columns: new[] { "legal_entity_id", "account_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_charge_term",
                schema: "bil",
                table: "charge",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "ux_charge_set_member",
                schema: "bil",
                table: "charge",
                columns: new[] { "set_id", "set_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_intake_exception",
                schema: "bil",
                table: "intake_exception",
                columns: new[] { "kind", "subject", "reason_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoice_account",
                schema: "bil",
                table: "invoice",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_fiscal_document",
                schema: "bil",
                table: "invoice",
                column: "fiscal_document_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_policy",
                schema: "bil",
                table: "invoice",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ux_invoice_number",
                schema: "bil",
                table: "invoice",
                columns: new[] { "legal_entity_id", "invoice_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_invoice_transaction",
                schema: "bil",
                table: "invoice",
                columns: new[] { "transaction_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoice_item_invoice",
                schema: "bil",
                table: "invoice_item",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ux_invoice_item_charge",
                schema: "bil",
                table: "invoice_item",
                column: "charge_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entry_account",
                schema: "bil",
                table: "ledger_entry",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entry_date",
                schema: "bil",
                table: "ledger_entry",
                columns: new[] { "legal_entity_id", "accounting_date" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_line_account",
                schema: "bil",
                table: "ledger_line",
                columns: new[] { "billing_account_id", "account_code" });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_line_account_code",
                schema: "bil",
                table: "ledger_line",
                column: "account_code");

            migrationBuilder.CreateIndex(
                name: "ux_ledger_line_no",
                schema: "bil",
                table: "ledger_line",
                columns: new[] { "entry_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_rule_credit_account",
                schema: "bil",
                table: "ledger_rule",
                column: "credit_account");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_rule_debit_account",
                schema: "bil",
                table: "ledger_rule",
                column: "debit_account");

            migrationBuilder.CreateIndex(
                name: "ix_plan_instance_account",
                schema: "bil",
                table: "plan_instance",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_plan_instance_policy",
                schema: "bil",
                table: "plan_instance",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ux_plan_instance_id",
                schema: "bil",
                table: "plan_instance",
                column: "plan_instance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_receipt_account",
                schema: "bil",
                table: "receipt",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ux_receipt_number",
                schema: "bil",
                table: "receipt",
                columns: new[] { "legal_entity_id", "receipt_number" },
                unique: true);

            migrationBuilder.Sql(BillingDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BillingDatabaseSql.Down);

            migrationBuilder.DropTable(
                name: "allocation",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "intake_exception",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "ledger_line",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "ledger_rule",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "plan_instance",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "invoice_item",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "receipt",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "ledger_entry",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "ledger_account",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "charge",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "invoice",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "billing_account",
                schema: "bil");
        }
    }
}
