using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <summary>GAP-SL3-BIL-VOID: the ledger rule of an IPT credit. Data only: no model change.</summary>
    [DbContext(typeof(BillingDbContext))]
    [Migration("20261019210000_CreditTax")]
    public partial class CreditTax : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(CreditTaxDatabaseSql.Rules);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(CreditTaxDatabaseSql.RulesDown);
    }
}
