using System;
using Microsoft.EntityFrameworkCore.Migrations;
using CoreIns.Modules.Reinsurance.Persistence;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <summary>
    /// BO-RI-10: premium accounting (deposit schedule revisions, adjustment premiums, sliding-scale commission and their single decisions).
    /// All four tables are append-only for every writer (seal triggers).
    /// </summary>
    public partial class AccountingTables : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounting_decision",
                schema: "ri",
                columns: table => new
                {
                    kind = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_decision", x => new { x.kind, x.subject_id });
                    table.CheckConstraint("ck_accounting_decision_decision", "decision IN ('APPROVED', 'REJECTED')");
                    table.CheckConstraint("ck_accounting_decision_kind", "kind IN ('ADJUSTMENT', 'COMMISSION')");
                });

            migrationBuilder.CreateTable(
                name: "commission",
                schema: "ri",
                columns: table => new
                {
                    commission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    ceded_premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    incurred_loss = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    loss_ratio_pct = table.Column<decimal>(type: "numeric(12,6)", nullable: false),
                    provisional_rate_pct = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    earned_rate_pct = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    provisional_commission = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    earned_commission = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    adjustment_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    scale = table.Column<string>(type: "jsonb", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission", x => x.commission_id);
                    table.CheckConstraint("ck_commission_amounts", "ceded_premium >= 0 AND incurred_loss >= 0 AND provisional_commission >= 0 AND earned_commission >= 0");
                    table.ForeignKey(
                        name: "fk_commission_contract",
                        column: x => x.contract_id,
                        principalSchema: "ri",
                        principalTable: "contract",
                        principalColumn: "contract_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deposit_schedule",
                schema: "ri",
                columns: table => new
                {
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer_no = table.Column<int>(type: "integer", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    minimum_premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    deposit_premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    instalments = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deposit_schedule", x => x.schedule_id);
                    table.CheckConstraint("ck_deposit_schedule_amounts", "minimum_premium >= 0 AND deposit_premium >= minimum_premium");
                    table.CheckConstraint("ck_deposit_schedule_layer", "layer_no >= 1");
                    table.CheckConstraint("ck_deposit_schedule_revision", "revision >= 1");
                    table.ForeignKey(
                        name: "fk_deposit_schedule_contract",
                        column: x => x.contract_id,
                        principalSchema: "ri",
                        principalTable: "contract",
                        principalColumn: "contract_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adjustment",
                schema: "ri",
                columns: table => new
                {
                    adjustment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer_no = table.Column<int>(type: "integer", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    subject_premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    rate_pct = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    calculated_premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    minimum_premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    deposit_premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    adjustment_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_adjustment", x => x.adjustment_id);
                    table.CheckConstraint("ck_adjustment_amounts", "subject_premium >= 0 AND calculated_premium >= 0 AND minimum_premium >= 0 AND deposit_premium >= 0");
                    table.ForeignKey(
                        name: "fk_adjustment_contract",
                        column: x => x.contract_id,
                        principalSchema: "ri",
                        principalTable: "contract",
                        principalColumn: "contract_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_adjustment_schedule",
                        column: x => x.schedule_id,
                        principalSchema: "ri",
                        principalTable: "deposit_schedule",
                        principalColumn: "schedule_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_adjustment_layer",
                schema: "ri",
                table: "adjustment",
                columns: new[] { "contract_id", "layer_no", "contract_year" });

            migrationBuilder.CreateIndex(
                name: "ix_adjustment_schedule",
                schema: "ri",
                table: "adjustment",
                column: "schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_contract",
                schema: "ri",
                table: "commission",
                columns: new[] { "contract_id", "contract_year", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_deposit_schedule_revision",
                schema: "ri",
                table: "deposit_schedule",
                columns: new[] { "contract_id", "layer_no", "revision" },
                unique: true);

            migrationBuilder.Sql(AccountingDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AccountingDatabaseSql.Down);

            migrationBuilder.DropTable(
                name: "accounting_decision",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "adjustment",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "commission",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "deposit_schedule",
                schema: "ri");
        }
    }
}
