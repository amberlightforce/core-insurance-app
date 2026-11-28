using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <summary>
    /// BO-BIL-07: rule set v7 (seed gr-test.finance.v7) adds the ALLOCATION_REVERSED rules AV-UNALLOCATED and AV-RECEIVABLE, the mirror of the
    /// ALLOCATED rules. Data only: no model change. Rule set v6 stays as applied (a seed is immutable once a migration applied it);
    /// v7 is effective from the same date and wins the tie by version number.
    /// </summary>
    [DbContext(typeof(FinanceDbContext))]
    [Migration("20261128090000_AllocationReversalPostings")]
    public partial class AllocationReversalPostings : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.Sql(FinanceSeed.GrTestV7));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.SupersedeV7);
    }
}
