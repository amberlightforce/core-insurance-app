using CoreIns.Modules.Claims.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <summary>SL4-CLM-PAYOPS: a claim payment can end Voided, Stopped or Returned (REQ-CLM-125..127).</summary>
    [DbContext(typeof(ClaimsDbContext))]
    [Migration("20261019210000_PaymentCorrections")]
    public partial class PaymentCorrections : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "ck_claim_payment_status", schema: "clm", table: "claim_payment");
            migrationBuilder.AddCheckConstraint(
                name: "ck_claim_payment_status", schema: "clm", table: "claim_payment",
                sql: "status IN ('PENDING', 'APPROVED', 'ON_HOLD', 'SUBMITTED', 'ISSUED', 'CLEARED', 'REJECTED', 'VOIDED', 'STOPPED', 'RETURNED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "ck_claim_payment_status", schema: "clm", table: "claim_payment");
            migrationBuilder.AddCheckConstraint(
                name: "ck_claim_payment_status", schema: "clm", table: "claim_payment",
                sql: "status IN ('PENDING', 'APPROVED', 'ON_HOLD', 'SUBMITTED', 'ISSUED', 'CLEARED', 'REJECTED')");
        }
    }
}
