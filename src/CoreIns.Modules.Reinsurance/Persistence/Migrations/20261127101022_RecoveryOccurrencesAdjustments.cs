using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryOccurrencesAdjustments : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "occurrence",
                schema: "ri",
                columns: table => new
                {
                    occurrence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    dissolved_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_occurrence", x => x.occurrence_id);
                });

            migrationBuilder.CreateTable(
                name: "recovery_adjustment",
                schema: "ri",
                columns: table => new
                {
                    adjustment_row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    adjustment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer_no = table.Column<int>(type: "integer", nullable: false),
                    participant_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incurred_delta = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    recorded_by = table.Column<string>(type: "text", nullable: false),
                    authority_check_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_adjustment", x => x.adjustment_row_id);
                });

            migrationBuilder.CreateTable(
                name: "occurrence_claim",
                schema: "ri",
                columns: table => new
                {
                    occurrence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    unlinked_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    unlink_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_occurrence_claim", x => new { x.occurrence_id, x.claim_id });
                    table.ForeignKey(
                        name: "fk_occurrence_claim_occurrence",
                        column: x => x.occurrence_id,
                        principalSchema: "ri",
                        principalTable: "occurrence",
                        principalColumn: "occurrence_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_occurrence_contract",
                schema: "ri",
                table: "occurrence",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "ux_occurrence_claim_active",
                schema: "ri",
                table: "occurrence_claim",
                column: "claim_id",
                unique: true,
                filter: "unlinked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_adjustment_contract_layer",
                schema: "ri",
                table: "recovery_adjustment",
                columns: new[] { "contract_id", "layer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recovery_adjustment_item",
                schema: "ri",
                table: "recovery_adjustment",
                column: "adjustment_id");
            migrationBuilder.Sql(OccurrenceDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(OccurrenceDatabaseSql.Down);

            migrationBuilder.DropTable(
                name: "occurrence_claim",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "recovery_adjustment",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "occurrence",
                schema: "ri");
        }
    }
}
