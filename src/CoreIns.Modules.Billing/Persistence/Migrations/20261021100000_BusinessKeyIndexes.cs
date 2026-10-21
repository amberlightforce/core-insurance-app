using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <summary>
    /// NX-EC-08: database-level business keys so a retry after the 7-day idempotency expiry cannot double-post: one bank receipt
    /// per bank statement line and one refund per disbursement. An existing database that already holds duplicates is refused
    /// with a message naming the key (nothing is deleted: money rows are never deduped silently); reconcile the rows, then retry.
    /// </summary>
    [DbContext(typeof(BillingDbContext))]
    [Migration("20261021100000_BusinessKeyIndexes")]
    public partial class BusinessKeyIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE n bigint;
                BEGIN
                    SELECT count(*) INTO n FROM (
                        SELECT 1 FROM bil.receipt WHERE bank_reference IS NOT NULL AND method = 'BANK_TRANSFER'
                        GROUP BY legal_entity_id, bank_reference HAVING count(*) > 1) d;
                    IF n > 0 THEN
                        RAISE EXCEPTION 'NX-EC-08: % bank reference(s) occur on more than one bil.receipt; reconcile the duplicate receipts (same legal_entity_id and bank_reference, method BANK_TRANSFER) before this migration can add ux_receipt_bank_reference.', n;
                    END IF;
                    SELECT count(*) INTO n FROM (
                        SELECT 1 FROM bil.refund WHERE disbursement_id IS NOT NULL GROUP BY disbursement_id HAVING count(*) > 1) d;
                    IF n > 0 THEN
                        RAISE EXCEPTION 'NX-EC-08: % disbursement(s) are linked to more than one bil.refund; reconcile before this migration can add ux_refund_disbursement.', n;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_receipt_bank_reference",
                schema: "bil",
                table: "receipt",
                columns: ["legal_entity_id", "bank_reference"],
                unique: true,
                filter: "bank_reference IS NOT NULL AND method = 'BANK_TRANSFER'");

            migrationBuilder.CreateIndex(
                name: "ux_refund_disbursement",
                schema: "bil",
                table: "refund",
                column: "disbursement_id",
                unique: true,
                filter: "disbursement_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ux_receipt_bank_reference", schema: "bil", table: "receipt");
            migrationBuilder.DropIndex(name: "ux_refund_disbursement", schema: "bil", table: "refund");
        }
    }
}
