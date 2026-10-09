using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreditNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_invoice_item_charge",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropIndex(
                name: "ux_invoice_transaction",
                schema: "bil",
                table: "invoice");

            migrationBuilder.AddColumn<string>(
                name: "cancellation_source",
                schema: "bil",
                table: "plan_instance",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "cancelled_effective",
                schema: "bil",
                table: "plan_instance",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "predecessor_term_id",
                schema: "bil",
                table: "plan_instance",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_source",
                schema: "bil",
                table: "ledger_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transaction_kind",
                schema: "bil",
                table: "ledger_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "treatment_rule_id",
                schema: "bil",
                table: "ledger_line",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_source",
                schema: "bil",
                table: "invoice_item",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "credits_item_id",
                schema: "bil",
                table: "invoice_item",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transaction_kind",
                schema: "bil",
                table: "invoice_item",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "treatment_rule_id",
                schema: "bil",
                table: "invoice_item",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "original_invoice_id",
                schema: "bil",
                table: "invoice",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_source",
                schema: "bil",
                table: "charge",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transaction_kind",
                schema: "bil",
                table: "charge",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "treatment_rule_id",
                schema: "bil",
                table: "charge",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "treatment_rule_version",
                schema: "bil",
                table: "charge",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "credit_application",
                schema: "bil",
                columns: table => new
                {
                    credit_application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_invoice_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_application", x => x.credit_application_id);
                    table.CheckConstraint("ck_credit_application_amount", "amount > 0");
                    table.CheckConstraint("ck_credit_application_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_credit_application_invoice_item", "target_kind <> 'INVOICE_ITEM' OR (target_invoice_id IS NOT NULL AND target_invoice_item_id IS NOT NULL)");
                    table.CheckConstraint("ck_credit_application_target", "target_kind IN ('INVOICE_ITEM')");
                    table.ForeignKey(
                        name: "fk_credit_application_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_credit_application_credit_item",
                        column: x => x.credit_item_id,
                        principalSchema: "bil",
                        principalTable: "invoice_item",
                        principalColumn: "invoice_item_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_credit_application_target_item",
                        column: x => x.target_invoice_item_id,
                        principalSchema: "bil",
                        principalTable: "invoice_item",
                        principalColumn: "invoice_item_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_instance_cancelled",
                schema: "bil",
                table: "plan_instance",
                sql: "(cancelled_effective IS NULL) = (cancellation_source IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_item_credits",
                schema: "bil",
                table: "invoice_item",
                column: "credits_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_credit_item_charge",
                schema: "bil",
                table: "invoice_item",
                columns: new[] { "charge_id", "credits_item_id" },
                unique: true,
                filter: "credits_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_invoice_item_charge",
                schema: "bil",
                table: "invoice_item",
                column: "charge_id",
                unique: true,
                filter: "credits_item_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_original",
                schema: "bil",
                table: "invoice",
                column: "original_invoice_id");

            migrationBuilder.CreateIndex(
                name: "ux_credit_note_reference",
                schema: "bil",
                table: "invoice",
                columns: new[] { "transaction_id", "original_invoice_id" },
                unique: true,
                filter: "kind = 'CREDIT_NOTE'");

            migrationBuilder.CreateIndex(
                name: "ux_invoice_transaction",
                schema: "bil",
                table: "invoice",
                columns: new[] { "transaction_id", "kind" },
                unique: true,
                filter: "kind = 'INVOICE'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_original",
                schema: "bil",
                table: "invoice",
                sql: "(kind = 'CREDIT_NOTE') = (original_invoice_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_credit_application_account",
                schema: "bil",
                table: "credit_application",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_credit_application_credit_item",
                schema: "bil",
                table: "credit_application",
                column: "credit_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_credit_application_target_item",
                schema: "bil",
                table: "credit_application",
                column: "target_invoice_item_id");

            migrationBuilder.AddForeignKey(
                name: "fk_invoice_original",
                schema: "bil",
                table: "invoice",
                column: "original_invoice_id",
                principalSchema: "bil",
                principalTable: "invoice",
                principalColumn: "invoice_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_invoice_item_credits",
                schema: "bil",
                table: "invoice_item",
                column: "credits_item_id",
                principalSchema: "bil",
                principalTable: "invoice_item",
                principalColumn: "invoice_item_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(BillingDatabaseSql.Credits);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BillingDatabaseSql.CreditsDown);

            migrationBuilder.DropForeignKey(
                name: "fk_invoice_original",
                schema: "bil",
                table: "invoice");

            migrationBuilder.DropForeignKey(
                name: "fk_invoice_item_credits",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropTable(
                name: "credit_application",
                schema: "bil");

            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_instance_cancelled",
                schema: "bil",
                table: "plan_instance");

            migrationBuilder.DropIndex(
                name: "ix_invoice_item_credits",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropIndex(
                name: "ux_credit_item_charge",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropIndex(
                name: "ux_invoice_item_charge",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropIndex(
                name: "ix_invoice_original",
                schema: "bil",
                table: "invoice");

            migrationBuilder.DropIndex(
                name: "ux_credit_note_reference",
                schema: "bil",
                table: "invoice");

            migrationBuilder.DropIndex(
                name: "ux_invoice_transaction",
                schema: "bil",
                table: "invoice");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_original",
                schema: "bil",
                table: "invoice");

            migrationBuilder.DropColumn(
                name: "cancellation_source",
                schema: "bil",
                table: "plan_instance");

            migrationBuilder.DropColumn(
                name: "cancelled_effective",
                schema: "bil",
                table: "plan_instance");

            migrationBuilder.DropColumn(
                name: "predecessor_term_id",
                schema: "bil",
                table: "plan_instance");

            migrationBuilder.DropColumn(
                name: "cancellation_source",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "transaction_kind",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "treatment_rule_id",
                schema: "bil",
                table: "ledger_line");

            migrationBuilder.DropColumn(
                name: "cancellation_source",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropColumn(
                name: "credits_item_id",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropColumn(
                name: "transaction_kind",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropColumn(
                name: "treatment_rule_id",
                schema: "bil",
                table: "invoice_item");

            migrationBuilder.DropColumn(
                name: "original_invoice_id",
                schema: "bil",
                table: "invoice");

            migrationBuilder.DropColumn(
                name: "cancellation_source",
                schema: "bil",
                table: "charge");

            migrationBuilder.DropColumn(
                name: "transaction_kind",
                schema: "bil",
                table: "charge");

            migrationBuilder.DropColumn(
                name: "treatment_rule_id",
                schema: "bil",
                table: "charge");

            migrationBuilder.DropColumn(
                name: "treatment_rule_version",
                schema: "bil",
                table: "charge");

            migrationBuilder.CreateIndex(
                name: "ux_invoice_item_charge",
                schema: "bil",
                table: "invoice_item",
                column: "charge_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_invoice_transaction",
                schema: "bil",
                table: "invoice",
                columns: new[] { "transaction_id", "kind" },
                unique: true);
        }
    }
}
