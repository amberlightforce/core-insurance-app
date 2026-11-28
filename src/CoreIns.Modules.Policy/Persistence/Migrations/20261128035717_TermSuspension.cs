using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TermSuspension : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_job_target_term",
                schema: "pol",
                table: "job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_type",
                schema: "pol",
                table: "job");

            migrationBuilder.AddColumn<DateTime>(
                name: "suspended_from",
                schema: "pol",
                table: "policy_term",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_target_term",
                schema: "pol",
                table: "job",
                sql: "job_type = 'SUBMISSION' OR (job_type IN ('POLICY_CHANGE', 'CANCELLATION', 'SUSPENSION', 'REACTIVATION') AND target_term_id IS NOT NULL) OR (job_type = 'RENEWAL' AND expiring_term_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_type",
                schema: "pol",
                table: "job",
                sql: "job_type IN ('SUBMISSION', 'POLICY_CHANGE', 'CANCELLATION', 'RENEWAL', 'SUSPENSION', 'REACTIVATION')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_job_target_term",
                schema: "pol",
                table: "job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_type",
                schema: "pol",
                table: "job");

            migrationBuilder.DropColumn(
                name: "suspended_from",
                schema: "pol",
                table: "policy_term");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_target_term",
                schema: "pol",
                table: "job",
                sql: "job_type = 'SUBMISSION' OR (job_type IN ('POLICY_CHANGE', 'CANCELLATION') AND target_term_id IS NOT NULL) OR (job_type = 'RENEWAL' AND expiring_term_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_type",
                schema: "pol",
                table: "job",
                sql: "job_type IN ('SUBMISSION', 'POLICY_CHANGE', 'CANCELLATION', 'RENEWAL')");
        }
    }
}
