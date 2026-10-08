using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClaimFinancials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "last_transaction_sequence",
                schema: "clm",
                table: "claim",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "policy_term_id",
                schema: "clm",
                table: "claim",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payee_account_view",
                schema: "clm",
                columns: table => new
                {
                    payee_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    masked_iban = table.Column<string>(type: "text", nullable: false),
                    verification_status = table.Column<string>(type: "text", nullable: false),
                    cooling_off_until = table.Column<DateOnly>(type: "date", nullable: true),
                    is_change = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payee_account_view", x => new { x.claim_id, x.payee_account_id });
                    table.CheckConstraint("ck_payee_account_view_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_payee_account_view_record_version", "record_version >= 1");
                    table.ForeignKey(
                        name: "fk_payee_account_view_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reserve_line",
                schema: "clm",
                columns: table => new
                {
                    reserve_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cost_type = table.Column<string>(type: "text", nullable: false),
                    cost_category = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    final_flag = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reserve_line", x => x.reserve_line_id);
                    table.CheckConstraint("ck_reserve_line_cost_type", "cost_type IN ('INDEMNITY', 'EXPENSE_ALLOCATED', 'EXPENSE_UNALLOCATED', 'STATUTORY_INTEREST')");
                    table.CheckConstraint("ck_reserve_line_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_reserve_line_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_reserve_line_record_version", "record_version >= 1");
                    table.ForeignKey(
                        name: "fk_reserve_line_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reserve_line_exposure",
                        column: x => x.exposure_id,
                        principalSchema: "clm",
                        principalTable: "exposure",
                        principalColumn: "exposure_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transaction_set",
                schema: "clm",
                columns: table => new
                {
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    basis_hash = table.Column<string>(type: "char(64)", nullable: false),
                    submitter = table.Column<string>(type: "text", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_type = table.Column<string>(type: "text", nullable: true),
                    approval_subject_type = table.Column<string>(type: "text", nullable: true),
                    approval_subject_id = table.Column<string>(type: "text", nullable: true),
                    approval_payload_hash = table.Column<string>(type: "char(64)", nullable: true),
                    approval_authority_type = table.Column<string>(type: "text", nullable: true),
                    approval_authority_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    approval_authority_cost_type = table.Column<string>(type: "text", nullable: true),
                    referral_role = table.Column<string>(type: "text", nullable: true),
                    authority_check_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    four_eyes = table.Column<bool>(type: "boolean", nullable: false),
                    approver = table.Column<string>(type: "text", nullable: true),
                    approver_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_txid = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "txid_current()"),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transaction_set", x => x.set_id);
                    table.CheckConstraint("ck_transaction_set_approved_shape", "status NOT IN ('APPROVED', 'POSTED') OR approved_at IS NOT NULL");
                    table.CheckConstraint("ck_transaction_set_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_transaction_set_pending_shape", "status <> 'PENDING_APPROVAL' OR (approval_request_id IS NOT NULL AND approval_payload_hash IS NOT NULL)");
                    table.CheckConstraint("ck_transaction_set_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_transaction_set_rejected_shape", "status <> 'REJECTED' OR rejection_reason IS NOT NULL");
                    table.CheckConstraint("ck_transaction_set_status", "status IN ('DRAFT', 'SUBMITTED', 'PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'POSTED')");
                    table.ForeignKey(
                        name: "fk_transaction_set_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "claim_payment",
                schema: "clm",
                columns: table => new
                {
                    claim_payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payee_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payee_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    masked_account = table.Column<string>(type: "text", nullable: true),
                    method = table.Column<string>(type: "text", nullable: false),
                    payment_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    hold_reason = table.Column<string>(type: "text", nullable: true),
                    disbursement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    disbursement_content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    approval_evidence_ref = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    issued_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    cleared_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_payment", x => x.claim_payment_id);
                    table.CheckConstraint("ck_claim_payment_amount", "amount > 0");
                    table.CheckConstraint("ck_claim_payment_hold_shape", "status <> 'ON_HOLD' OR hold_reason IS NOT NULL");
                    table.CheckConstraint("ck_claim_payment_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_claim_payment_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_claim_payment_status", "status IN ('PENDING', 'APPROVED', 'ON_HOLD', 'SUBMITTED', 'ISSUED', 'CLEARED', 'REJECTED')");
                    table.CheckConstraint("ck_claim_payment_submitted_shape", "status NOT IN ('SUBMITTED', 'ISSUED', 'CLEARED') OR disbursement_id IS NOT NULL");
                    table.CheckConstraint("ck_claim_payment_type", "payment_type IN ('PARTIAL', 'FINAL')");
                    table.ForeignKey(
                        name: "fk_claim_payment_exposure",
                        column: x => x.exposure_id,
                        principalSchema: "clm",
                        principalTable: "exposure",
                        principalColumn: "exposure_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_claim_payment_set",
                        column: x => x.set_id,
                        principalSchema: "clm",
                        principalTable: "transaction_set",
                        principalColumn: "set_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_transaction",
                schema: "clm",
                columns: table => new
                {
                    txn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reserve_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    txn_number = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    functional_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    functional_currency = table.Column<string>(type: "char(3)", nullable: false),
                    group_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    group_currency = table.Column<string>(type: "char(3)", nullable: false),
                    fx_rate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    eroding = table.Column<bool>(type: "boolean", nullable: true),
                    payment_type = table.Column<string>(type: "text", nullable: true),
                    claim_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reverses_txn_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    proposed = table.Column<bool>(type: "boolean", nullable: false),
                    transaction_date = table.Column<DateOnly>(type: "date", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_transaction", x => x.txn_id);
                    table.CheckConstraint("ck_financial_transaction_amount", "amount <> 0");
                    table.CheckConstraint("ck_financial_transaction_currencies", "currency ~ '^[A-Z]{3}$' AND functional_currency ~ '^[A-Z]{3}$' AND group_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_financial_transaction_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_financial_transaction_kind", "kind IN ('RESERVE', 'PAYMENT')");
                    table.CheckConstraint("ck_financial_transaction_payment_shape", "kind <> 'PAYMENT' OR (amount > 0 AND eroding IS NOT NULL AND payment_type IS NOT NULL AND claim_payment_id IS NOT NULL)");
                    table.CheckConstraint("ck_financial_transaction_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_financial_transaction_reserve_shape", "kind <> 'RESERVE' OR (eroding IS NULL AND payment_type IS NULL AND claim_payment_id IS NULL)");
                    table.CheckConstraint("ck_financial_transaction_sequence", "sequence >= 1");
                    table.ForeignKey(
                        name: "fk_financial_transaction_line",
                        column: x => x.reserve_line_id,
                        principalSchema: "clm",
                        principalTable: "reserve_line",
                        principalColumn: "reserve_line_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transaction_reverses",
                        column: x => x.reverses_txn_id,
                        principalSchema: "clm",
                        principalTable: "financial_transaction",
                        principalColumn: "txn_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transaction_set",
                        column: x => x.set_id,
                        principalSchema: "clm",
                        principalTable: "transaction_set",
                        principalColumn: "set_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_claim_transaction_sequence",
                schema: "clm",
                table: "claim",
                sql: "last_transaction_sequence >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_claim_payment_claim",
                schema: "clm",
                table: "claim_payment",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "IX_claim_payment_exposure_id",
                schema: "clm",
                table: "claim_payment",
                column: "exposure_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_payment_set",
                schema: "clm",
                table: "claim_payment",
                column: "set_id");

            migrationBuilder.CreateIndex(
                name: "ux_claim_payment_disbursement",
                schema: "clm",
                table: "claim_payment",
                column: "disbursement_id",
                unique: true,
                filter: "disbursement_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_financial_transaction_line",
                schema: "clm",
                table: "financial_transaction",
                column: "reserve_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_financial_transaction_reverses_txn_id",
                schema: "clm",
                table: "financial_transaction",
                column: "reverses_txn_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_transaction_set",
                schema: "clm",
                table: "financial_transaction",
                column: "set_id");

            migrationBuilder.CreateIndex(
                name: "ux_financial_transaction_claim_sequence",
                schema: "clm",
                table: "financial_transaction",
                columns: new[] { "claim_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reserve_line_claim",
                schema: "clm",
                table: "reserve_line",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ux_reserve_line_key",
                schema: "clm",
                table: "reserve_line",
                columns: new[] { "exposure_id", "cost_type", "cost_category", "currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transaction_set_claim",
                schema: "clm",
                table: "transaction_set",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ux_transaction_set_approval_request",
                schema: "clm",
                table: "transaction_set",
                column: "approval_request_id",
                unique: true,
                filter: "approval_request_id IS NOT NULL");

            // D-ARC-34: append-only, sealed ledger lines; frozen header, payments and lines (ClaimsFinancialSql).
            migrationBuilder.Sql(ClaimsFinancialSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ClaimsFinancialSql.Down);

            migrationBuilder.DropTable(
                name: "claim_payment",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "financial_transaction",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "payee_account_view",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "reserve_line",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "transaction_set",
                schema: "clm");

            migrationBuilder.DropCheckConstraint(
                name: "ck_claim_transaction_sequence",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "last_transaction_sequence",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "policy_term_id",
                schema: "clm",
                table: "claim");
        }
    }
}
