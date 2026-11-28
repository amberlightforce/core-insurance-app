using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <summary>
    /// BO-BIL-13: rule set v8 (seed gr-test.finance.v8) adds the WRITTEN_OFF rules WO-RECEIVABLE, WO-EXPENSE and WO-TOLERANCE and the placeholder
    /// accounts GL-6210 and GL-6220. Data only: no model change. Rule set v7 stays as applied (a seed is immutable once a migration applied it);
    /// v8 is effective from the same date and wins the tie by version number.
    /// </summary>
    [DbContext(typeof(FinanceDbContext))]
    [Migration("20261128100000_WriteOffPostings")]
    public partial class WriteOffPostings : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.Sql(FinanceSeed.GrTestV8));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.SupersedeV8);
    }
}
