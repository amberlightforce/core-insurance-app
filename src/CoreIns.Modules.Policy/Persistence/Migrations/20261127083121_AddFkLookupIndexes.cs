using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFkLookupIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_withdrawal_term",
                schema: "pol",
                table: "withdrawal",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "ix_segment_policy",
                schema: "pol",
                table: "segment",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_psr_exception_policy",
                schema: "pol",
                table: "psr_exception",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_policy_transaction_term",
                schema: "pol",
                table: "policy_transaction",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "ix_policy_account",
                schema: "pol",
                table: "policy",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_pack_rollback_exception_policy",
                schema: "pol",
                table: "pack_rollback_exception",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_oos_correction_term",
                schema: "pol",
                table: "oos_correction",
                column: "term_id");

            migrationBuilder.CreateIndex(
                name: "ix_charge_line_policy",
                schema: "pol",
                table: "charge_line",
                column: "policy_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_withdrawal_term",
                schema: "pol",
                table: "withdrawal");

            migrationBuilder.DropIndex(
                name: "ix_segment_policy",
                schema: "pol",
                table: "segment");

            migrationBuilder.DropIndex(
                name: "ix_psr_exception_policy",
                schema: "pol",
                table: "psr_exception");

            migrationBuilder.DropIndex(
                name: "ix_policy_transaction_term",
                schema: "pol",
                table: "policy_transaction");

            migrationBuilder.DropIndex(
                name: "ix_policy_account",
                schema: "pol",
                table: "policy");

            migrationBuilder.DropIndex(
                name: "ix_pack_rollback_exception_policy",
                schema: "pol",
                table: "pack_rollback_exception");

            migrationBuilder.DropIndex(
                name: "ix_oos_correction_term",
                schema: "pol",
                table: "oos_correction");

            migrationBuilder.DropIndex(
                name: "ix_charge_line_policy",
                schema: "pol",
                table: "charge_line");
        }
    }
}
