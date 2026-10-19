using System;
using CoreIns.Modules.Reinsurance.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "claim_amount",
                schema: "ri",
                columns: table => new
                {
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cost_type = table.Column<string>(type: "text", nullable: false),
                    paid = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    open_reserve = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    recovery_open = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    realised = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    reserve_sequence = table.Column<long>(type: "bigint", nullable: false),
                    recovery_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_amount", x => new { x.claim_id, x.exposure_id, x.cost_type });
                });

            migrationBuilder.CreateTable(
                name: "claim_event",
                schema: "ri",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_sequence = table.Column<long>(type: "bigint", nullable: false),
                    applied_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_event", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "claim_exposure",
                schema: "ri",
                columns: table => new
                {
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: true),
                    coverage_code = table.Column<string>(type: "text", nullable: true),
                    sii_lob = table.Column<string>(type: "text", nullable: true),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_exposure", x => new { x.claim_id, x.exposure_id });
                });

            migrationBuilder.CreateTable(
                name: "claim_view",
                schema: "ri",
                columns: table => new
                {
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_code = table.Column<string>(type: "text", nullable: true),
                    loss_date = table.Column<DateOnly>(type: "date", nullable: true),
                    closed = table.Column<bool>(type: "boolean", nullable: false),
                    closed_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_view", x => x.claim_id);
                });

            migrationBuilder.CreateTable(
                name: "intake_exception",
                schema: "ri",
                columns: table => new
                {
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_exception", x => x.exception_id);
                });

            migrationBuilder.CreateTable(
                name: "recovery_batch",
                schema: "ri",
                columns: table => new
                {
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    calc_hash = table.Column<string>(type: "text", nullable: false),
                    engine_version = table.Column<string>(type: "text", nullable: false),
                    contract_version = table.Column<string>(type: "text", nullable: false),
                    trace = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_batch", x => x.batch_id);
                });

            migrationBuilder.CreateTable(
                name: "recovery",
                schema: "ri",
                columns: table => new
                {
                    recovery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer_no = table.Column<int>(type: "integer", nullable: false),
                    participant_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incurred_delta = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    paid_delta = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    outstanding_delta = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    sii_lob = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery", x => x.recovery_id);
                    table.CheckConstraint("ck_recovery_state", "state IN ('CALCULATED', 'POSTED', 'REVERSED')");
                    table.ForeignKey(
                        name: "fk_recovery_batch",
                        column: x => x.batch_id,
                        principalSchema: "ri",
                        principalTable: "recovery_batch",
                        principalColumn: "batch_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_claim_event_claim",
                schema: "ri",
                table: "claim_event",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_exposure_contract",
                schema: "ri",
                table: "claim_exposure",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "ix_intake_exception_claim",
                schema: "ri",
                table: "intake_exception",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_batch",
                schema: "ri",
                table: "recovery",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_claim",
                schema: "ri",
                table: "recovery",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_contract_claim",
                schema: "ri",
                table: "recovery",
                columns: new[] { "contract_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recovery_batch_contract",
                schema: "ri",
                table: "recovery_batch",
                columns: new[] { "contract_id", "recorded_at" });
        
            migrationBuilder.Sql(RecoveryDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RecoveryDatabaseSql.Down);

            migrationBuilder.DropTable(
                name: "claim_amount",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "claim_event",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "claim_exposure",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "claim_view",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "intake_exception",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "recovery",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "recovery_batch",
                schema: "ri");
        }
    }
}
