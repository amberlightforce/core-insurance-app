using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualJournals : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manual_journal",
                schema: "fin",
                columns: table => new
                {
                    manual_journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number_seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    status = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    book = table.Column<string>(type: "text", nullable: false),
                    accounting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    evidence_ref = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    submitted_by = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_reason = table.Column<string>(type: "text", nullable: true),
                    posted_journal_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_journal", x => x.manual_journal_id);
                    table.CheckConstraint("ck_manual_journal_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_manual_journal_evidence", "length(btrim(evidence_ref)) > 0");
                    table.CheckConstraint("ck_manual_journal_posted", "status <> 'POSTED' OR cardinality(posted_journal_ids) >= 1");
                    table.CheckConstraint("ck_manual_journal_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_manual_journal_status", "status IN ('DRAFT', 'SUBMITTED', 'APPROVED', 'REJECTED', 'WITHDRAWN', 'POSTED')");
                });

            migrationBuilder.CreateTable(
                name: "manual_journal_line",
                schema: "fin",
                columns: table => new
                {
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manual_journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    account_code = table.Column<string>(type: "text", nullable: false),
                    side = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_journal_line", x => x.line_id);
                    table.CheckConstraint("ck_manual_journal_line_amount", "amount > 0");
                    table.CheckConstraint("ck_manual_journal_line_no", "line_no >= 1");
                    table.CheckConstraint("ck_manual_journal_line_side", "side IN ('DEBIT', 'CREDIT')");
                    table.ForeignKey(
                        name: "fk_manual_journal_line_journal",
                        column: x => x.manual_journal_id,
                        principalSchema: "fin",
                        principalTable: "manual_journal",
                        principalColumn: "manual_journal_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_manual_journal_number",
                schema: "fin",
                table: "manual_journal",
                columns: new[] { "legal_entity_id", "number_seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_manual_journal_line_no",
                schema: "fin",
                table: "manual_journal_line",
                columns: new[] { "manual_journal_id", "line_no" },
                unique: true);

            migrationBuilder.Sql(FinanceSql.ManualJournalGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS fin.guard_manual_journal_line() CASCADE; DROP FUNCTION IF EXISTS fin.guard_manual_journal() CASCADE;");

            migrationBuilder.DropTable(
                name: "manual_journal_line",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "manual_journal",
                schema: "fin");
        }
    }
}
