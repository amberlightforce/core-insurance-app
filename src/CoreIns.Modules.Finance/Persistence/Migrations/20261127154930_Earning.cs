using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Earning : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Insurance revenue (earned premium) for every book that carries the LRC account; the earning engine posts the earned part of GL-2110 here.
            migrationBuilder.Sql("""
                INSERT INTO fin.gl_account (legal_entity_code, book, account_code, name_el, name_en, account_type, normal_balance, code_origin, system_only, status, source)
                SELECT legal_entity_code, book, 'GL-4110', 'Έσοδα ασφάλισης – κατακτημένα ασφάλιστρα', 'Insurance revenue – earned premium', 'INCOME', 'CREDIT',
                       'TECHNICAL_PLACEHOLDER', true, 'ACTIVE', 'BO-FIN-06a: earned premium (not in PRD-09 §7.1.4)'
                  FROM fin.gl_account WHERE account_code = 'GL-2110'
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.AddColumn<DateOnly>(
                name: "term_end",
                schema: "fin",
                table: "policy_context",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "term_start",
                schema: "fin",
                table: "policy_context",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "earning_run",
                schema: "fin",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_code = table.Column<string>(type: "text", nullable: false),
                    as_of = table.Column<DateOnly>(type: "date", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    run_by = table.Column<string>(type: "text", nullable: false),
                    policies_processed = table.Column<int>(type: "integer", nullable: false),
                    terms_skipped = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    earned_to_date = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    posted_delta = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    journal_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_earning_run", x => x.run_id);
                    table.CheckConstraint("ck_earning_run_period", "period_code ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                    table.CheckConstraint("ck_earning_run_state", "state IN ('DONE')");
                });

            migrationBuilder.CreateTable(
                name: "earned_premium",
                schema: "fin",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    book = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_number = table.Column<string>(type: "text", nullable: false),
                    term_start = table.Column<DateOnly>(type: "date", nullable: false),
                    term_end = table.Column<DateOnly>(type: "date", nullable: false),
                    written_gross = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    written_net = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    days_earned = table.Column<int>(type: "integer", nullable: false),
                    term_days = table.Column<int>(type: "integer", nullable: false),
                    earned_to_date = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    delta = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_earned_premium", x => new { x.run_id, x.policy_term_id, x.book, x.currency });
                    table.CheckConstraint("ck_earned_premium_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_earned_premium_days", "term_days > 0 AND days_earned >= 0 AND days_earned <= term_days");
                    table.ForeignKey(
                        name: "fk_earned_premium_run",
                        column: x => x.run_id,
                        principalSchema: "fin",
                        principalTable: "earning_run",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_earned_premium_term",
                schema: "fin",
                table: "earned_premium",
                columns: new[] { "legal_entity_id", "policy_term_id", "book" });

            migrationBuilder.CreateIndex(
                name: "ix_earning_run_period",
                schema: "fin",
                table: "earning_run",
                columns: new[] { "legal_entity_id", "period_code", "as_of" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "earned_premium",
                schema: "fin");

            migrationBuilder.DropTable(
                name: "earning_run",
                schema: "fin");

            migrationBuilder.DropColumn(
                name: "term_end",
                schema: "fin",
                table: "policy_context");

            migrationBuilder.DropColumn(
                name: "term_start",
                schema: "fin",
                table: "policy_context");
        }
    }
}
