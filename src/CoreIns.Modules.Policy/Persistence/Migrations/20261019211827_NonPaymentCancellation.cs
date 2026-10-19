using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NonPaymentCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nonpay_cancellation",
                schema: "pol",
                columns: table => new
                {
                    nonpay_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clock_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    effective_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    overdue_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    notice_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proof_ref = table.Column<string>(type: "text", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nonpay_cancellation", x => x.nonpay_id);
                    table.CheckConstraint("ck_nonpay_cancellation_status", "status IN ('SCHEDULED', 'EXECUTED', 'EXCEPTION', 'RESCINDED', 'HELD')");
                });

            migrationBuilder.CreateTable(
                name: "psr_exception",
                schema: "pol",
                columns: table => new
                {
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nonpay_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    raised_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_psr_exception", x => x.exception_id);
                    table.CheckConstraint("ck_psr_exception_reason", "reason IN ('VULNERABLE_CUSTOMER', 'HOLD')");
                    table.CheckConstraint("ck_psr_exception_status", "status IN ('PENDING', 'PROCEED', 'HOLD', 'RESCINDED')");
                    table.ForeignKey(
                        name: "fk_psr_exception_nonpay",
                        column: x => x.nonpay_id,
                        principalSchema: "pol",
                        principalTable: "nonpay_cancellation",
                        principalColumn: "nonpay_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_nonpay_cancellation_due",
                schema: "pol",
                table: "nonpay_cancellation",
                columns: new[] { "status", "effective_at" });

            migrationBuilder.CreateIndex(
                name: "ix_nonpay_cancellation_request",
                schema: "pol",
                table: "nonpay_cancellation",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ux_nonpay_cancellation_policy_term_clock",
                schema: "pol",
                table: "nonpay_cancellation",
                columns: new[] { "policy_id", "term_id", "clock_instance_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_psr_exception_queue",
                schema: "pol",
                table: "psr_exception",
                columns: new[] { "legal_entity_id", "status", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ux_psr_exception_nonpay",
                schema: "pol",
                table: "psr_exception",
                column: "nonpay_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "psr_exception",
                schema: "pol");

            migrationBuilder.DropTable(
                name: "nonpay_cancellation",
                schema: "pol");
        }
    }
}
