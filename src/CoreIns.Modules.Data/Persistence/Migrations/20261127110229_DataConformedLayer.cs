using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataConformedLayer : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rpt_conformed");

            migrationBuilder.CreateTable(
                name: "fact_claim_movement",
                schema: "rpt_conformed",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_key = table.Column<string>(type: "text", nullable: false),
                    movement_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    policy_term_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    valid_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fact_claim_movement", x => x.row_id);
                });

            migrationBuilder.CreateTable(
                name: "fact_policy_term",
                schema: "rpt_conformed",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    valid_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fact_policy_term", x => x.row_id);
                });

            migrationBuilder.CreateTable(
                name: "fact_premium",
                schema: "rpt_conformed",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    valid_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fact_premium", x => x.row_id);
                });

            migrationBuilder.CreateTable(
                name: "projected_event",
                schema: "rpt_conformed",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    projected_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projected_event", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fact_claim_movement_key",
                schema: "rpt_conformed",
                table: "fact_claim_movement",
                columns: new[] { "legal_entity", "movement_key" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_claim_movement_valid",
                schema: "rpt_conformed",
                table: "fact_claim_movement",
                columns: new[] { "legal_entity", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_policy_term_term",
                schema: "rpt_conformed",
                table: "fact_policy_term",
                columns: new[] { "legal_entity", "term_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_policy_term_valid",
                schema: "rpt_conformed",
                table: "fact_policy_term",
                columns: new[] { "legal_entity", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_premium_key",
                schema: "rpt_conformed",
                table: "fact_premium",
                columns: new[] { "legal_entity", "invoice_id", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_premium_valid",
                schema: "rpt_conformed",
                table: "fact_premium",
                columns: new[] { "legal_entity", "valid_from" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fact_claim_movement",
                schema: "rpt_conformed");

            migrationBuilder.DropTable(
                name: "fact_policy_term",
                schema: "rpt_conformed");

            migrationBuilder.DropTable(
                name: "fact_premium",
                schema: "rpt_conformed");

            migrationBuilder.DropTable(
                name: "projected_event",
                schema: "rpt_conformed");
        }
    }
}
