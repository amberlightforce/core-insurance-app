using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SealLedgerAndFreezeAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BillingDatabaseSql.Seal);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BillingDatabaseSql.SealDown);
        }
    }
}
