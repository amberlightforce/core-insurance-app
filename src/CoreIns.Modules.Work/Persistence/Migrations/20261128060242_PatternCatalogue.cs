using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PatternCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activity_pattern",
                schema: "wrk",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    queue_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sla_policy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    target_minutes = table.Column<int>(type: "integer", nullable: false),
                    escalation_minutes = table.Column<int>(type: "integer", nullable: false),
                    gate_blocking = table.Column<bool>(type: "boolean", nullable: false),
                    raising_modules = table.Column<string>(type: "text", nullable: false),
                    suggested_assignee = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    amount_band_percent = table.Column<string>(type: "text", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity_pattern", x => x.code);
                    table.CheckConstraint("ck_activity_pattern_minutes", "target_minutes > 0 AND escalation_minutes >= target_minutes");
                    table.CheckConstraint("ck_activity_pattern_priority", "priority IN ('NORMAL', 'HIGH')");
                });

            migrationBuilder.CreateTable(
                name: "queue",
                schema: "wrk",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    roles = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queue", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_activity_pattern_queue",
                schema: "wrk",
                table: "activity_pattern",
                column: "queue_code");

            // Seed: exactly the queues and patterns that were code constants (WorkCatalog.SeedQueues / SeedPatterns); a test keeps them equal.
            migrationBuilder.Sql("""
                INSERT INTO wrk.queue (code, title, roles) VALUES
                ('SCREENING', 'Sanctions screening review', 'Staff.ComplianceOfficer,Platform.Admin'),
                ('PSR', 'Policy service representative review', 'Staff.UnderwritingManager,Staff.Underwriter,Platform.Admin'),
                ('PACK-ROLLBACK-REVIEW', 'Pack rollback review', 'Staff.UnderwritingManager,Platform.Admin'),
                ('UW-REFERRAL', 'Underwriting referral follow-up', 'Staff.UnderwritingManager,Staff.Underwriter,Platform.Admin');
                """);
            migrationBuilder.Sql("""
                INSERT INTO wrk.activity_pattern (code, version, title, category, priority, queue_code, sla_policy, target_minutes, escalation_minutes, gate_blocking, raising_modules, active) VALUES
                ('WRK-PTY-SCREENING-HIT', '1', 'Review a potential sanctions screening hit', 'REVIEW', 'HIGH', 'SCREENING', 'WRK-SLA-SCREENING', 1440, 2880, false, 'PTY', true),
                ('WRK-MKT-PACK-ROLLBACK-REVIEW', '1', 'Review transactions recorded under a rolled-back pack version', 'REVIEW', 'HIGH', 'PACK-ROLLBACK-REVIEW', 'WRK-SLA-PACK-ROLLBACK', 2880, 4320, false, 'MKT,POL', true),
                ('WRK-UW-REFERRAL-INFORMATION', '1', 'Underwriting referral waits for information', 'REVIEW', 'NORMAL', 'UW-REFERRAL', 'WRK-SLA-UW-REFERRAL', 4320, 7200, false, 'UW', true),
                ('WRK-UW-REFERRAL-REFER-UP', '1', 'Underwriting referral referred up to a higher authority', 'REVIEW', 'HIGH', 'UW-REFERRAL', 'WRK-SLA-UW-REFERRAL', 1440, 2880, false, 'UW', true),
                ('WRK-POL-NONPAY-PSR-EXCEPTION', '1', 'Decide a non-payment cancellation held for the PSR', 'REVIEW', 'HIGH', 'PSR', 'WRK-SLA-PSR', 1440, 2880, false, 'POL', true),
                ('WRK-BIL-NONPAY-PSR-HOLD', '1', 'Vulnerable-customer hold on a delinquency case', 'REVIEW', 'HIGH', 'PSR', 'WRK-SLA-PSR', 1440, 2880, false, 'BIL', true),
                ('WRK-BIL-NONPAY-NOTICE-RETURNED', '1', 'Non-payment notice came back undelivered', 'REVIEW', 'NORMAL', 'PSR', 'WRK-SLA-PSR', 4320, 7200, false, 'BIL', true);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_pattern",
                schema: "wrk");

            migrationBuilder.DropTable(
                name: "queue",
                schema: "wrk");
        }
    }
}
