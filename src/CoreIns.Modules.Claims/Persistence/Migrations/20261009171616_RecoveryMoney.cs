using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryMoney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_transaction_kind",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_transaction_payment_shape",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropCheckConstraint(
                name: "ck_claim_payment_submitted_shape",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.AddColumn<string>(
                name: "evidence_kind",
                schema: "clm",
                table: "transaction_set",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_ref",
                schema: "clm",
                table: "transaction_set",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "recovery_id",
                schema: "clm",
                table: "financial_transaction",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "statutory_clocks",
                schema: "clm",
                table: "exposure",
                type: "text",
                nullable: false,
                defaultValue: "NOT_TRACKED");

            migrationBuilder.AddColumn<Guid>(
                name: "counterparty_insurer_party_id",
                schema: "clm",
                table: "claim_payment",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_mark",
                schema: "clm",
                table: "claim_payment",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "fs_statement_id",
                schema: "clm",
                table: "claim_payment",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reissue_of",
                schema: "clm",
                table: "claim_payment",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reversal_of",
                schema: "clm",
                table: "claim_payment",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "accident_in_greece",
                schema: "clm",
                table: "claim",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "counterparty_insurer_party_id",
                schema: "clm",
                table: "claim",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "fault_insured_pct",
                schema: "clm",
                table: "claim",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fault_source",
                schema: "clm",
                table: "claim",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "joint_accident_report",
                schema: "clm",
                table: "claim",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "vehicle_count",
                schema: "clm",
                table: "claim",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fs_statement",
                schema: "clm",
                columns: table => new
                {
                    fs_statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "text", nullable: false),
                    counterparty_insurer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_reference = table.Column<string>(type: "text", nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    disbursement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    receivable_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fs_statement", x => x.fs_statement_id);
                    table.CheckConstraint("ck_fs_statement_currency", "currency = 'EUR'");
                    table.CheckConstraint("ck_fs_statement_direction", "direction IN ('PAYABLE','RECEIVABLE')");
                    table.CheckConstraint("ck_fs_statement_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_fs_statement_net", "net_amount >= 0");
                    table.CheckConstraint("ck_fs_statement_period", "period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                    table.CheckConstraint("ck_fs_statement_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_fs_statement_status", "status IN ('PENDING','REQUESTED','SETTLED','REJECTED')");
                });

            migrationBuilder.CreateTable(
                name: "recovery",
                schema: "clm",
                columns: table => new
                {
                    recovery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    counterparty_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    milestones = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    salvage_estimate = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    buyer_party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sale_price = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    receivable_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_reference = table.Column<string>(type: "text", nullable: true),
                    fs_case_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fs_statement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    demanded_on = table.Column<DateOnly>(type: "date", nullable: true),
                    write_off_reason = table.Column<string>(type: "text", nullable: true),
                    allocation_rule = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery", x => x.recovery_id);
                    table.CheckConstraint("ck_recovery_allocation", "allocation_rule IN ('PRO_RATA_PAID','SPECIFIED')");
                    table.CheckConstraint("ck_recovery_amount", "expected_amount >= 0 AND (salvage_estimate IS NULL OR salvage_estimate >= 0) AND (sale_price IS NULL OR sale_price >= 0)");
                    table.CheckConstraint("ck_recovery_currency", "currency = 'EUR'");
                    table.CheckConstraint("ck_recovery_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_recovery_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_recovery_status", "status IN ('OPEN','DEMANDED','AGREED','DISPUTED','IN_ARBITRATION','IN_LITIGATION','CLOSED','WRITTEN_OFF')");
                    table.CheckConstraint("ck_recovery_type", "type IN ('SUBROGATION','SALVAGE','FRIENDLY_SETTLEMENT')");
                    table.ForeignKey(
                        name: "fk_recovery_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recovery_exposure",
                        column: x => x.exposure_id,
                        principalSchema: "clm",
                        principalTable: "exposure",
                        principalColumn: "exposure_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fs_case",
                schema: "clm",
                columns: table => new
                {
                    fs_case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recovery_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role = table.Column<string>(type: "text", nullable: false),
                    counterparty_insurer_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    eligibility_result = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    eligibility_rule_id = table.Column<string>(type: "text", nullable: true),
                    eligibility_rule_version = table.Column<string>(type: "text", nullable: true),
                    legal_status = table.Column<string>(type: "text", nullable: true),
                    provisional = table.Column<bool>(type: "boolean", nullable: false),
                    clearing_value = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: false),
                    clearing_reference = table.Column<string>(type: "text", nullable: true),
                    notification_reference = table.Column<string>(type: "text", nullable: true),
                    dispute_reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    statement_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fs_case", x => x.fs_case_id);
                    table.CheckConstraint("ck_fs_case_currency", "currency = 'EUR'");
                    table.CheckConstraint("ck_fs_case_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_fs_case_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_fs_case_role", "role IN ('OWN_INSURER','AT_FAULT_INSURER')");
                    table.CheckConstraint("ck_fs_case_rule", "eligibility_result IS NULL OR (eligibility_rule_id IS NOT NULL AND eligibility_rule_version IS NOT NULL AND legal_status IS NOT NULL)");
                    table.CheckConstraint("ck_fs_case_status", "status IN ('ELIGIBILITY_PENDING','ELIGIBLE','NOT_ELIGIBLE','SUBMITTED','ACCEPTED','DISPUTED','REJECTED','SETTLED','RECONCILED')");
                    table.CheckConstraint("ck_fs_case_value", "clearing_value IS NULL OR clearing_value >= 0");
                    table.ForeignKey(
                        name: "fk_fs_case_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fs_case_exposure",
                        column: x => x.exposure_id,
                        principalSchema: "clm",
                        principalTable: "exposure",
                        principalColumn: "exposure_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fs_case_recovery",
                        column: x => x.recovery_id,
                        principalSchema: "clm",
                        principalTable: "recovery",
                        principalColumn: "recovery_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fs_statement_line",
                schema: "clm",
                columns: table => new
                {
                    fs_statement_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fs_statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_reference = table.Column<string>(type: "text", nullable: false),
                    clearing_reference = table.Column<string>(type: "text", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    match_status = table.Column<string>(type: "text", nullable: false),
                    fs_case_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recovery_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claim_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    exception_reason = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fs_statement_line", x => x.fs_statement_line_id);
                    table.CheckConstraint("ck_fs_statement_line_amount", "amount > 0 AND currency = 'EUR'");
                    table.CheckConstraint("ck_fs_statement_line_direction", "direction IN ('PAYABLE','RECEIVABLE')");
                    table.CheckConstraint("ck_fs_statement_line_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_fs_statement_line_match", "match_status IN ('UNMATCHED','MATCHED','EXCEPTION','SETTLED')");
                    table.CheckConstraint("ck_fs_statement_line_record_version", "record_version >= 1");
                    table.ForeignKey(
                        name: "fk_fs_statement_line_case",
                        column: x => x.fs_case_id,
                        principalSchema: "clm",
                        principalTable: "fs_case",
                        principalColumn: "fs_case_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fs_statement_line_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fs_statement_line_payment",
                        column: x => x.claim_payment_id,
                        principalSchema: "clm",
                        principalTable: "claim_payment",
                        principalColumn: "claim_payment_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fs_statement_line_recovery",
                        column: x => x.recovery_id,
                        principalSchema: "clm",
                        principalTable: "recovery",
                        principalColumn: "recovery_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fs_statement_line_statement",
                        column: x => x.fs_statement_id,
                        principalSchema: "clm",
                        principalTable: "fs_statement",
                        principalColumn: "fs_statement_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_transaction_set_evidence",
                schema: "clm",
                table: "transaction_set",
                columns: new[] { "legal_entity_id", "evidence_kind", "evidence_ref" },
                unique: true,
                filter: "evidence_ref IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transaction_set_evidence",
                schema: "clm",
                table: "transaction_set",
                sql: "(evidence_kind IS NULL AND evidence_ref IS NULL) OR (evidence_kind IN ('BIL_ALLOCATION','FS_NOTIFICATION','FS_STATEMENT_LINE','DISBURSEMENT_OUTCOME') AND evidence_ref IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_financial_transaction_recovery_id",
                schema: "clm",
                table: "financial_transaction",
                column: "recovery_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_transaction_kind",
                schema: "clm",
                table: "financial_transaction",
                sql: "kind IN ('RESERVE', 'RECOVERY_RESERVE', 'RECOVERY', 'PAYMENT')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_transaction_payment_shape",
                schema: "clm",
                table: "financial_transaction",
                sql: "kind <> 'PAYMENT' OR ((amount > 0 OR (amount < 0 AND reverses_txn_id IS NOT NULL)) AND eroding IS NOT NULL AND payment_type IS NOT NULL AND claim_payment_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_transaction_recovery_shape",
                schema: "clm",
                table: "financial_transaction",
                sql: "kind NOT IN ('RECOVERY_RESERVE','RECOVERY') OR (recovery_id IS NOT NULL AND eroding IS NULL AND payment_type IS NULL AND claim_payment_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_claim_payment_reversal_of",
                schema: "clm",
                table: "claim_payment",
                column: "reversal_of");

            migrationBuilder.CreateIndex(
                name: "ux_claim_payment_reissue",
                schema: "clm",
                table: "claim_payment",
                column: "reissue_of",
                unique: true,
                filter: "reissue_of IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_claim_payment_clearing",
                schema: "clm",
                table: "claim_payment",
                sql: "method <> 'CLEARING' OR disbursement_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_claim_payment_clearing_counterparty",
                schema: "clm",
                table: "claim_payment",
                sql: "method <> 'CLEARING' OR counterparty_insurer_party_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_claim_payment_submitted_shape",
                schema: "clm",
                table: "claim_payment",
                sql: "status NOT IN ('SUBMITTED', 'ISSUED', 'CLEARED') OR disbursement_id IS NOT NULL OR (method = 'CLEARING' AND fs_statement_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_claim_fault_pct",
                schema: "clm",
                table: "claim",
                sql: "fault_insured_pct IS NULL OR fault_insured_pct BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_claim_vehicle_count",
                schema: "clm",
                table: "claim",
                sql: "vehicle_count IS NULL OR vehicle_count >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_fs_case_exposure_id",
                schema: "clm",
                table: "fs_case",
                column: "exposure_id");

            migrationBuilder.CreateIndex(
                name: "IX_fs_case_recovery_id",
                schema: "clm",
                table: "fs_case",
                column: "recovery_id");

            migrationBuilder.CreateIndex(
                name: "ux_fs_case_notification",
                schema: "clm",
                table: "fs_case",
                columns: new[] { "legal_entity_id", "notification_reference" },
                unique: true,
                filter: "notification_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_fs_case_open_claim_role",
                schema: "clm",
                table: "fs_case",
                columns: new[] { "claim_id", "role" },
                unique: true,
                filter: "status NOT IN ('NOT_ELIGIBLE','REJECTED','RECONCILED')");

            migrationBuilder.CreateIndex(
                name: "ux_fs_statement_period_counterparty",
                schema: "clm",
                table: "fs_statement",
                columns: new[] { "legal_entity_id", "period", "counterparty_insurer_party_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fs_statement_line_claim_id",
                schema: "clm",
                table: "fs_statement_line",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "IX_fs_statement_line_claim_payment_id",
                schema: "clm",
                table: "fs_statement_line",
                column: "claim_payment_id");

            migrationBuilder.CreateIndex(
                name: "IX_fs_statement_line_fs_case_id",
                schema: "clm",
                table: "fs_statement_line",
                column: "fs_case_id");

            migrationBuilder.CreateIndex(
                name: "IX_fs_statement_line_recovery_id",
                schema: "clm",
                table: "fs_statement_line",
                column: "recovery_id");

            migrationBuilder.CreateIndex(
                name: "ux_fs_statement_line_reference",
                schema: "clm",
                table: "fs_statement_line",
                columns: new[] { "fs_statement_id", "external_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recovery_claim",
                schema: "clm",
                table: "recovery",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "IX_recovery_exposure_id",
                schema: "clm",
                table: "recovery",
                column: "exposure_id");

            migrationBuilder.CreateIndex(
                name: "ux_recovery_receivable",
                schema: "clm",
                table: "recovery",
                column: "receivable_id",
                unique: true,
                filter: "receivable_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_claim_payment_reissue",
                schema: "clm",
                table: "claim_payment",
                column: "reissue_of",
                principalSchema: "clm",
                principalTable: "claim_payment",
                principalColumn: "claim_payment_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_claim_payment_reversal",
                schema: "clm",
                table: "claim_payment",
                column: "reversal_of",
                principalSchema: "clm",
                principalTable: "claim_payment",
                principalColumn: "claim_payment_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_transaction_recovery",
                schema: "clm",
                table: "financial_transaction",
                column: "recovery_id",
                principalSchema: "clm",
                principalTable: "recovery",
                principalColumn: "recovery_id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(RecoveryDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RecoveryDatabaseSql.Down);

            migrationBuilder.DropForeignKey(
                name: "fk_claim_payment_reissue",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropForeignKey(
                name: "fk_claim_payment_reversal",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_transaction_recovery",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropTable(
                name: "fs_statement_line",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "fs_case",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "fs_statement",
                schema: "clm");

            migrationBuilder.DropTable(
                name: "recovery",
                schema: "clm");

            migrationBuilder.DropIndex(
                name: "ux_transaction_set_evidence",
                schema: "clm",
                table: "transaction_set");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transaction_set_evidence",
                schema: "clm",
                table: "transaction_set");

            migrationBuilder.DropIndex(
                name: "IX_financial_transaction_recovery_id",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_transaction_kind",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_transaction_payment_shape",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_transaction_recovery_shape",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropIndex(
                name: "IX_claim_payment_reversal_of",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropIndex(
                name: "ux_claim_payment_reissue",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_claim_payment_clearing",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_claim_payment_clearing_counterparty",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_claim_payment_submitted_shape",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_claim_fault_pct",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropCheckConstraint(
                name: "ck_claim_vehicle_count",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "evidence_kind",
                schema: "clm",
                table: "transaction_set");

            migrationBuilder.DropColumn(
                name: "evidence_ref",
                schema: "clm",
                table: "transaction_set");

            migrationBuilder.DropColumn(
                name: "recovery_id",
                schema: "clm",
                table: "financial_transaction");

            migrationBuilder.DropColumn(
                name: "statutory_clocks",
                schema: "clm",
                table: "exposure");

            migrationBuilder.DropColumn(
                name: "counterparty_insurer_party_id",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropColumn(
                name: "fiscal_mark",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropColumn(
                name: "fs_statement_id",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropColumn(
                name: "reissue_of",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropColumn(
                name: "reversal_of",
                schema: "clm",
                table: "claim_payment");

            migrationBuilder.DropColumn(
                name: "accident_in_greece",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "counterparty_insurer_party_id",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "fault_insured_pct",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "fault_source",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "joint_accident_report",
                schema: "clm",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "vehicle_count",
                schema: "clm",
                table: "claim");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_transaction_kind",
                schema: "clm",
                table: "financial_transaction",
                sql: "kind IN ('RESERVE', 'PAYMENT')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_transaction_payment_shape",
                schema: "clm",
                table: "financial_transaction",
                sql: "kind <> 'PAYMENT' OR (amount > 0 AND eroding IS NOT NULL AND payment_type IS NOT NULL AND claim_payment_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_claim_payment_submitted_shape",
                schema: "clm",
                table: "claim_payment",
                sql: "status NOT IN ('SUBMITTED', 'ISSUED', 'CLEARED') OR disbursement_id IS NOT NULL");
        }
    }
}
