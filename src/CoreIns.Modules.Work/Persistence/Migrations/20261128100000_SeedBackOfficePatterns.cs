using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Work.Persistence.Migrations
{
    /// <summary>
    /// Seeds the CMP and DOC back-office queues and patterns (BO-WRK-04): data only, the model is unchanged. Mirrors
    /// <c>WorkCatalog.SeedQueues</c> / <c>SeedPatterns</c>; a test keeps them equal. An admin edit made before this runs is never overwritten.
    /// </summary>
    [DbContext(typeof(WorkDbContext))]
    [Migration("20261128100000_SeedBackOfficePatterns")]
    public partial class SeedBackOfficePatterns : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO wrk.queue (code, title, roles) VALUES
                ('CMP-OPS', 'Compliance operations', 'Staff.ComplianceOfficer,Platform.Admin'),
                ('DOC-OPS', 'Document operations', 'Staff.ComplianceOfficer,Staff.UnderwritingManager,Platform.Admin'),
                ('FIN-OPS', 'Finance operations', 'Staff.Finance,Staff.ComplianceOfficer,Platform.Admin')
                ON CONFLICT (code) DO NOTHING;
                """);
            migrationBuilder.Sql("""
                INSERT INTO wrk.activity_pattern (code, version, title, category, priority, queue_code, sla_policy, target_minutes, escalation_minutes, gate_blocking, raising_modules, active) VALUES
                ('WRK-CMP-MISSING-RULE', '1', 'Complaint has no matching handling rule', 'REVIEW', 'HIGH', 'CMP-OPS', 'WRK-SLA-CMP-OPS', 1440, 2880, false, 'CMP', true),
                ('WRK-CMP-COMPLAINT-ESCALATED', '1', 'Complaint escalated to the alternative dispute resolution body', 'REVIEW', 'HIGH', 'CMP-OPS', 'WRK-SLA-CMP-OPS', 1440, 2880, false, 'CMP', true),
                ('WRK-DOC-DELIVERY-CONFLICT', '1', 'Document delivery providers disagree on the outcome', 'REVIEW', 'NORMAL', 'DOC-OPS', 'WRK-SLA-DOC-OPS', 4320, 7200, false, 'DOC', true),
                ('WRK-CMP-FISCAL-REJECTED', '1', 'Fiscal document rejected by the tax authority channel', 'REVIEW', 'HIGH', 'FIN-OPS', 'WRK-SLA-FIN-OPS', 1440, 2880, false, 'CMP', true),
                ('WRK-CMP-DSAR-TASK-FAILED', '1', 'A data subject request task failed in a module', 'REVIEW', 'HIGH', 'CMP-OPS', 'WRK-SLA-CMP-OPS', 1440, 2880, false, 'CMP', true)
                ON CONFLICT (code) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM wrk.activity_pattern WHERE code IN ('WRK-CMP-MISSING-RULE','WRK-CMP-COMPLAINT-ESCALATED','WRK-DOC-DELIVERY-CONFLICT','WRK-CMP-FISCAL-REJECTED','WRK-CMP-DSAR-TASK-FAILED');");
            migrationBuilder.Sql("DELETE FROM wrk.queue WHERE code IN ('CMP-OPS','DOC-OPS','FIN-OPS');");
        }
    }
}
