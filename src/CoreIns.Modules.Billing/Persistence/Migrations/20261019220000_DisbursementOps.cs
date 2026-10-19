using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <summary>
    /// SL4-BIL-DISBOPS: the billing-ledger rules of DISBURSEMENT_REVERSED (stop after release, void, bank reject, return). Data
    /// only: no model change, so no model snapshot change.
    /// </summary>
    [DbContext(typeof(BillingDbContext))]
    [Migration("20261019220000_DisbursementOps")]
    public partial class DisbursementOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DisbursementOpsDatabaseSql.Rules);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DisbursementOpsDatabaseSql.RulesDown);
    }
}
