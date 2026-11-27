using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFkLookupIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_respread_billing_account",
                schema: "bil",
                table: "respread",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_credit_policy",
                schema: "bil",
                table: "refund_credit",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_payee_party",
                schema: "bil",
                table: "refund",
                column: "payee_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_receivable_claim",
                schema: "bil",
                table: "receivable",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_line_claim",
                schema: "bil",
                table: "ledger_line",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_line_invoice",
                schema: "bil",
                table: "ledger_line",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_line_policy",
                schema: "bil",
                table: "ledger_line",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_item_term",
                schema: "bil",
                table: "invoice_item",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_term",
                schema: "bil",
                table: "invoice",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "ix_disbursement_payee_party",
                schema: "bil",
                table: "disbursement",
                column: "payee_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_delinquency_case_policy",
                schema: "bil",
                table: "delinquency_case",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_credit_application_target_invoice",
                schema: "bil",
                table: "credit_application",
                column: "target_invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_charge_policy",
                schema: "bil",
                table: "charge",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_allocation_invoice",
                schema: "bil",
                table: "allocation",
                column: "invoice_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_respread_billing_account",
                schema: "bil",
                table: "respread");

            migrationBuilder.DropIndex(
                name: "ix_refund_credit_policy",
                schema: "bil",
                table: "refund_credit");

            migrationBuilder.DropIndex(
                name: "ix_refund_payee_party",
                schema: "bil",
                table: "refund");

            migrationBuilder.DropIndex(
                name: "ix_receivable_claim",
                schema: "bil",
                table: "receivable");

            migrationBuilder.DropIndex(
                name: "ix_ledger_line_claim",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropIndex(
                name: "ix_ledger_line_invoice",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropIndex(
                name: "ix_ledger_line_policy",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropIndex(
                name: "ix_invoice_item_term",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropIndex(
                name: "ix_invoice_term",
                schema: "bil",
                table: "invoice");

            migrationBuilder.DropIndex(
                name: "ix_disbursement_payee_party",
                schema: "bil",
                table: "disbursement");

            migrationBuilder.DropIndex(
                name: "ix_delinquency_case_policy",
                schema: "bil",
                table: "delinquency_case");

            migrationBuilder.DropIndex(
                name: "ix_credit_application_target_invoice",
                schema: "bil",
                table: "credit_application");

            migrationBuilder.DropIndex(
                name: "ix_charge_policy",
                schema: "bil",
                table: "charge");

            migrationBuilder.DropIndex(
                name: "ix_allocation_invoice",
                schema: "bil",
                table: "allocation");
        }
    }
}
