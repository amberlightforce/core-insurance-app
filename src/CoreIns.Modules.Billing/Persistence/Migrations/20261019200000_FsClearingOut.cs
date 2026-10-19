using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <summary>
    /// SL4-BIL-FSOUT: the billing-ledger rules of an FS_CLEARING net payable (released LA-24 to LA-13, cleared LA-13 to LA-10, FIN
    /// v4 expects exactly these entry types) and the fix of the receivable cash leg: the receipt intake already debits cash
    /// (RECEIVED: LA-10 to LA-11), so RECEIVABLE_COLLECTED releases the unapplied cash LA-11 to the receivable LA-28.
    /// Data only: no model change, so no model snapshot change.
    /// </summary>
    [DbContext(typeof(BillingDbContext))]
    [Migration("20261019200000_FsClearingOut")]
    public partial class FsClearingOut : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FsClearingDatabaseSql.Rules);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(FsClearingDatabaseSql.RulesDown);
    }
}
