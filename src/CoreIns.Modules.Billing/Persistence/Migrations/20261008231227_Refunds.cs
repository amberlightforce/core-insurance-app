using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Refunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_application_invoice_item",
                schema: "bil",
                table: "credit_application");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_application_target",
                schema: "bil",
                table: "credit_application");

            migrationBuilder.AddColumn<string>(
                name: "business_ref",
                schema: "bil",
                table: "disbursement",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "refund_id",
                schema: "bil",
                table: "credit_application",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "refund",
                schema: "bil",
                columns: table => new
                {
                    refund_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    approval_state = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    payee_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payee_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payee_changed = table.Column<bool>(type: "boolean", nullable: false),
                    payee_account_changed_by = table.Column<string>(type: "text", nullable: false),
                    payout_method = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    selected_credit_notes = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    credit_set_key = table.Column<string>(type: "char(64)", nullable: false),
                    resubmits_refund_id = table.Column<Guid>(type: "uuid", nullable: true),
                    participants = table.Column<string[]>(type: "text[]", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_content_hash = table.Column<string>(type: "char(64)", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_comment = table.Column<string>(type: "text", nullable: true),
                    disbursement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposed_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    paid_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund", x => x.refund_id);
                    table.CheckConstraint("ck_refund_amount", "amount > 0");
                    table.CheckConstraint("ck_refund_approval", "(approval_state = 'PENDING') = (state = 'PENDING_APPROVAL')");
                    table.CheckConstraint("ck_refund_approval_state", "approval_state IN ('NOT_REQUIRED', 'PENDING', 'APPROVED', 'REJECTED')");
                    table.CheckConstraint("ck_refund_credit_set_key", "credit_set_key ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_refund_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_refund_hash", "approval_content_hash IS NULL OR approval_content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_refund_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_refund_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_refund_state", "state IN ('PROPOSED', 'PENDING_APPROVAL', 'APPROVED', 'HELD', 'DISBURSING', 'AWAITING_PROOF', 'PAID', 'REJECTED', 'RETURNED')");
                    table.ForeignKey(
                        name: "fk_refund_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_payee_account",
                        column: x => x.payee_account_id,
                        principalSchema: "bil",
                        principalTable: "payee_account",
                        principalColumn: "payee_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refund_credit",
                schema: "bil",
                columns: table => new
                {
                    refund_credit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_type = table.Column<string>(type: "text", nullable: false),
                    charge_category = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_credit", x => x.refund_credit_id);
                    table.CheckConstraint("ck_refund_credit_amount", "amount > 0");
                    table.CheckConstraint("ck_refund_credit_currency", "currency ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "fk_refund_credit_item",
                        column: x => x.credit_item_id,
                        principalSchema: "bil",
                        principalTable: "invoice_item",
                        principalColumn: "invoice_item_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_credit_refund",
                        column: x => x.refund_id,
                        principalSchema: "bil",
                        principalTable: "refund",
                        principalColumn: "refund_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refund_netting",
                schema: "bil",
                columns: table => new
                {
                    refund_netting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_invoice_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refund_netting", x => x.refund_netting_id);
                    table.CheckConstraint("ck_refund_netting_amount", "amount > 0");
                    table.CheckConstraint("ck_refund_netting_currency", "currency ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "fk_refund_netting_credit_item",
                        column: x => x.credit_item_id,
                        principalSchema: "bil",
                        principalTable: "invoice_item",
                        principalColumn: "invoice_item_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_netting_refund",
                        column: x => x.refund_id,
                        principalSchema: "bil",
                        principalTable: "refund",
                        principalColumn: "refund_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_netting_target_item",
                        column: x => x.target_invoice_item_id,
                        principalSchema: "bil",
                        principalTable: "invoice_item",
                        principalColumn: "invoice_item_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_disbursement_business_ref",
                schema: "bil",
                table: "disbursement",
                columns: new[] { "legal_entity_id", "payee_account_id", "amount", "currency", "source_type", "business_ref" },
                unique: true,
                filter: "state NOT IN ('REJECTED', 'STOPPED', 'VOIDED', 'RETURNED') AND business_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_credit_application_refund",
                schema: "bil",
                table: "credit_application",
                column: "refund_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_application_invoice_item",
                schema: "bil",
                table: "credit_application",
                sql: "target_kind NOT IN ('INVOICE_ITEM', 'NETTING') OR (target_invoice_id IS NOT NULL AND target_invoice_item_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_application_refund",
                schema: "bil",
                table: "credit_application",
                sql: "(target_kind IN ('REFUND', 'NETTING')) = (refund_id IS NOT NULL) AND (target_kind <> 'REFUND' OR (target_invoice_id IS NULL AND target_invoice_item_id IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_application_target",
                schema: "bil",
                table: "credit_application",
                sql: "target_kind IN ('INVOICE_ITEM', 'REFUND', 'NETTING')");

            migrationBuilder.CreateIndex(
                name: "ix_refund_account",
                schema: "bil",
                table: "refund",
                columns: new[] { "legal_entity_id", "billing_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refund_approval_request",
                schema: "bil",
                table: "refund",
                column: "approval_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_payee_account_id",
                schema: "bil",
                table: "refund",
                column: "payee_account_id");

            migrationBuilder.CreateIndex(
                name: "ux_refund_open_per_account",
                schema: "bil",
                table: "refund",
                column: "billing_account_id",
                unique: true,
                filter: "state NOT IN ('REJECTED', 'PAID', 'RETURNED')");

            migrationBuilder.CreateIndex(
                name: "ix_refund_credit_item",
                schema: "bil",
                table: "refund_credit",
                column: "credit_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_refund_credit_item",
                schema: "bil",
                table: "refund_credit",
                columns: new[] { "refund_id", "credit_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refund_netting_credit_item_id",
                schema: "bil",
                table: "refund_netting",
                column: "credit_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_netting_target_invoice_item_id",
                schema: "bil",
                table: "refund_netting",
                column: "target_invoice_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_refund_netting_pair",
                schema: "bil",
                table: "refund_netting",
                columns: new[] { "refund_id", "credit_item_id", "target_invoice_item_id" },
                unique: true);

            migrationBuilder.Sql(BillingDatabaseSql.Refunds);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BillingDatabaseSql.RefundsDown);

            migrationBuilder.DropTable(
                name: "refund_credit",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "refund_netting",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "refund",
                schema: "bil");

            migrationBuilder.DropIndex(
                name: "ux_disbursement_business_ref",
                schema: "bil",
                table: "disbursement");

            migrationBuilder.DropIndex(
                name: "ix_credit_application_refund",
                schema: "bil",
                table: "credit_application");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_application_invoice_item",
                schema: "bil",
                table: "credit_application");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_application_refund",
                schema: "bil",
                table: "credit_application");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_application_target",
                schema: "bil",
                table: "credit_application");

            migrationBuilder.DropColumn(
                name: "business_ref",
                schema: "bil",
                table: "disbursement");

            migrationBuilder.DropColumn(
                name: "refund_id",
                schema: "bil",
                table: "credit_application");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_application_invoice_item",
                schema: "bil",
                table: "credit_application",
                sql: "target_kind <> 'INVOICE_ITEM' OR (target_invoice_id IS NOT NULL AND target_invoice_item_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_application_target",
                schema: "bil",
                table: "credit_application",
                sql: "target_kind IN ('INVOICE_ITEM')");
        }
    }
}
