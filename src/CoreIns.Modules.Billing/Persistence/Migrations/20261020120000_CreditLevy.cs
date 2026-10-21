using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <summary>NX-HD-06: the ledger rule of a levy credit. Data only: no model change.</summary>
    [DbContext(typeof(BillingDbContext))]
    [Migration("20261020120000_CreditLevy")]
    public partial class CreditLevy : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(CreditTaxDatabaseSql.LevyRules);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(CreditTaxDatabaseSql.LevyRulesDown);
    }
}
