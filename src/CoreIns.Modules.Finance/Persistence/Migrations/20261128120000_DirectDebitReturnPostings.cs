using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <summary>
    /// BO-BIL-06b: rule set v9 (seed gr-test.finance.v9) adds the RECEIPT_RETURNED rules RR-CASH and RR-UNALLOCATED. Data only: no model change. Rule set v8 stays as
    /// applied (a seed is immutable once a migration applied it); v9 is effective from the same date and wins the tie by version number.
    /// </summary>
    [DbContext(typeof(FinanceDbContext))]
    [Migration("20261128120000_DirectDebitReturnPostings")]
    public partial class DirectDebitReturnPostings : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.Sql(FinanceSeed.GrTestV9));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.SupersedeV9);
    }
}
