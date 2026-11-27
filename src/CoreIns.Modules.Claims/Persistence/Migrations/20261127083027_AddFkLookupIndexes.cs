using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFkLookupIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_financial_transaction_claim_payment",
                schema: "clm",
                table: "financial_transaction",
                column: "claim_payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_transaction_exposure",
                schema: "clm",
                table: "financial_transaction",
                column: "exposure_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_payment_payee_party",
                schema: "clm",
                table: "claim_payment",
                column: "payee_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_policy_term",
                schema: "clm",
                table: "claim",
                column: "policy_term_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_financial_transaction_claim_payment",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropIndex(
                name: "ix_financial_transaction_exposure",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropIndex(
                name: "ix_claim_payment_payee_party",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropIndex(
                name: "ix_claim_policy_term",
                schema: "clm",
                table: "claim");
        }
    }
}
