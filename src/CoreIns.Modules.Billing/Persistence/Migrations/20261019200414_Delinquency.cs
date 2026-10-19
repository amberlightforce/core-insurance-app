using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Delinquency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delinquency_case",
                schema: "bil",
                columns: table => new
                {
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    plan_code = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    overdue_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    notice_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notification_date = table.Column<DateOnly>(type: "date", nullable: true),
                    clock_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deadline = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    promise_date = table.Column<DateOnly>(type: "date", nullable: true),
                    hold_reason = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    cancellation_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    opened_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delinquency_case", x => x.case_id);
                    table.CheckConstraint("ck_delinquency_case_amount", "overdue_amount >= 0");
                    table.CheckConstraint("ck_delinquency_case_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_delinquency_case_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_delinquency_case_state", "state IN ('NOTICE_REQUESTED', 'CLOCK_RUNNING', 'NOTICE_RETURNED', 'ON_HOLD', 'ARRANGED', 'CANCELLATION_REQUESTED', 'RESOLVED')");
                    table.CheckConstraint("ck_delinquency_case_trigger", "trigger IN ('PAYMENT_RETURN', 'OVERDUE')");
                    table.ForeignKey(
                        name: "fk_delinquency_case_account",
                        column: x => x.billing_account_id,
                        principalSchema: "bil",
                        principalTable: "billing_account",
                        principalColumn: "billing_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delinquency_contact",
                schema: "bil",
                columns: table => new
                {
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    recorded_by = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delinquency_contact", x => x.contact_id);
                    table.ForeignKey(
                        name: "fk_delinquency_contact_case",
                        column: x => x.case_id,
                        principalSchema: "bil",
                        principalTable: "delinquency_case",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delinquency_case_account",
                schema: "bil",
                table: "delinquency_case",
                columns: new[] { "legal_entity_id", "billing_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_delinquency_case_billing_account_id",
                schema: "bil",
                table: "delinquency_case",
                column: "billing_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_delinquency_case_clock",
                schema: "bil",
                table: "delinquency_case",
                column: "clock_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_delinquency_case_document",
                schema: "bil",
                table: "delinquency_case",
                column: "notice_document_id");

            migrationBuilder.CreateIndex(
                name: "ux_delinquency_case_open_per_term",
                schema: "bil",
                table: "delinquency_case",
                column: "term_id",
                unique: true,
                filter: "state IN ('NOTICE_REQUESTED', 'CLOCK_RUNNING', 'NOTICE_RETURNED', 'ON_HOLD', 'ARRANGED')");

            migrationBuilder.CreateIndex(
                name: "ix_delinquency_contact_case",
                schema: "bil",
                table: "delinquency_contact",
                column: "case_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delinquency_contact",
                schema: "bil");

            migrationBuilder.DropTable(
                name: "delinquency_case",
                schema: "bil");
        }
    }
}
