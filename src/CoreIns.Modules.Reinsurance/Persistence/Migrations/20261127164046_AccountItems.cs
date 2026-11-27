using System;
using Microsoft.EntityFrameworkCore.Migrations;
using CoreIns.Modules.Reinsurance.Persistence;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <summary>BO-RI-11: the technical account (append-only items; seal triggers).</summary>
    public partial class AccountItems : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_item",
                schema: "ri",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_year = table.Column<int>(type: "integer", nullable: false),
                    layer_no = table.Column<int>(type: "integer", nullable: true),
                    reinsurer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    source_kind = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    source_group = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    period = table.Column<string>(type: "text", nullable: false),
                    item_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reversal_of = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_item", x => x.item_id);
                    table.CheckConstraint("ck_account_item_kind", "kind IN ('CEDED_PREMIUM', 'DEPOSIT_INSTALMENT', 'PREMIUM_ADJUSTMENT', 'COMMISSION', 'RECOVERY')");
                    table.CheckConstraint("ck_account_item_period", "period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                    table.ForeignKey(
                        name: "fk_account_item_contract",
                        column: x => x.contract_id,
                        principalSchema: "ri",
                        principalTable: "contract",
                        principalColumn: "contract_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_item_contract",
                schema: "ri",
                table: "account_item",
                columns: new[] { "contract_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ix_account_item_reinsurer",
                schema: "ri",
                table: "account_item",
                columns: new[] { "legal_entity_id", "reinsurer_party_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ux_account_item_reversal",
                schema: "ri",
                table: "account_item",
                column: "reversal_of",
                unique: true,
                filter: "reversal_of IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_account_item_source",
                schema: "ri",
                table: "account_item",
                columns: new[] { "kind", "source_id", "reinsurer_party_id" },
                unique: true);

            migrationBuilder.Sql(AccountItemDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AccountItemDatabaseSql.Down);
            migrationBuilder.DropTable(
                name: "account_item",
                schema: "ri");
        }
    }
}
