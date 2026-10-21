using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <summary>
    /// NX-HD-06: rule set v5 (seed gr-test.finance.v5) adds CW-LEVY-PAYABLE. Data only: no model change. Rule set v4 stays as applied
    /// (a seed is immutable once a migration applied it); v5 is effective from the same date and wins the tie by version number.
    /// </summary>
    [DbContext(typeof(FinanceDbContext))]
    [Migration("20261021100000_LevyCreditPosting")]
    public partial class LevyCreditPosting : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.Sql(FinanceSeed.GrTestV5));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.SupersedeV5);
    }
}
