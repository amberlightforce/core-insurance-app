using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <summary>
    /// BO-BIL-10: rule set v6 (seed gr-test.finance.v6) adds the DISBURSEMENT_RELEASED and DISBURSEMENT_REVERSED rules of the non-claim
    /// disbursement sources. Data only: no model change. Rule set v5 stays as applied (a seed is immutable once a migration applied it);
    /// v6 is effective from the same date and wins the tie by version number.
    /// </summary>
    [DbContext(typeof(FinanceDbContext))]
    [Migration("20261127230000_DisbursementBatchPostings")]
    public partial class DisbursementBatchPostings : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.Sql(FinanceSeed.GrTestV6));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FinanceSeed.SupersedeV6);
    }
}
