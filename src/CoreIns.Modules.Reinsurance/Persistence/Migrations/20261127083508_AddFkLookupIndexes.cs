using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFkLookupIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_recovery_batch_claim",
                schema: "ri",
                table: "recovery_batch",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_layer",
                schema: "ri",
                table: "recovery",
                column: "layer_id");

            migrationBuilder.CreateIndex(
                name: "ix_premium_delta_policy",
                schema: "ri",
                table: "premium_delta",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_participation_broker_party",
                schema: "ri",
                table: "participation",
                column: "broker_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_view_policy",
                schema: "ri",
                table: "claim_view",
                column: "policy_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_recovery_batch_claim",
                schema: "ri",
                table: "recovery_batch");

            migrationBuilder.DropIndex(
                name: "ix_recovery_layer",
                schema: "ri",
                table: "recovery");

            migrationBuilder.DropIndex(
                name: "ix_premium_delta_policy",
                schema: "ri",
                table: "premium_delta");

            migrationBuilder.DropIndex(
                name: "ix_participation_broker_party",
                schema: "ri",
                table: "participation");

            migrationBuilder.DropIndex(
                name: "ix_claim_view_policy",
                schema: "ri",
                table: "claim_view");
        }
    }
}
