using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StatutoryOffer : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "statutory_offer",
                schema: "clm",
                columns: table => new
                {
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: true),
                    issued_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    delivered_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    delivery_evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clock_legal_status = table.Column<string>(type: "text", nullable: false),
                    payment_clock_id = table.Column<Guid>(type: "uuid", nullable: true),
                    repair_clock_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    repair_agreed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    payment_set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statutory_offer", x => x.offer_id);
                    table.CheckConstraint("ck_statutory_offer_accepted", "status <> 'ACCEPTED' OR (accepted_at IS NOT NULL AND payment_set_id IS NOT NULL)");
                    table.CheckConstraint("ck_statutory_offer_amount", "(kind = 'REASONED_OFFER') = (amount IS NOT NULL AND amount > 0 AND currency IS NOT NULL)");
                    table.CheckConstraint("ck_statutory_offer_delivery", "delivered_at >= issued_at");
                    table.CheckConstraint("ck_statutory_offer_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_statutory_offer_kind", "kind IN ('REASONED_OFFER', 'REASONED_REPLY')");
                    table.CheckConstraint("ck_statutory_offer_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_statutory_offer_repair", "status <> 'REPAIR_AGREED' OR (repair_agreed_at IS NOT NULL AND repair_clock_id IS NOT NULL)");
                    table.CheckConstraint("ck_statutory_offer_reply_closed", "kind = 'REASONED_OFFER' OR status = 'ISSUED'");
                    table.CheckConstraint("ck_statutory_offer_status", "status IN ('ISSUED', 'ACCEPTED', 'REPAIR_AGREED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_statutory_offer_claim",
                schema: "clm",
                table: "statutory_offer",
                columns: new[] { "legal_entity_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ux_statutory_offer_exposure",
                schema: "clm",
                table: "statutory_offer",
                column: "exposure_id",
                unique: true,
                filter: "kind = 'REASONED_OFFER'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "statutory_offer",
                schema: "clm");
        }
    }
}
