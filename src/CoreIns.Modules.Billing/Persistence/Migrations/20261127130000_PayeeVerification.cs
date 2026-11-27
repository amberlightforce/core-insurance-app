using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <summary>
    /// BO-BIL-09: lets <c>bil.PayeeAccount.verify</c> record a newer verification-of-payee result on an account while the bank
    /// details stay frozen. SQL only: no model change, so no model snapshot change.
    /// </summary>
    [DbContext(typeof(BillingDbContext))]
    [Migration("20261127130000_PayeeVerification")]
    public partial class PayeeReverification : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(PayeeVerificationDatabaseSql.Up);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(PayeeVerificationDatabaseSql.Down);
    }
}
