using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PackRollbackExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "stale_reason",
                schema: "pol",
                table: "quote_version",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pack_rollback_exception",
                schema: "pol",
                columns: table => new
                {
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pack = table.Column<string>(type: "text", nullable: false),
                    from_version = table.Column<string>(type: "text", nullable: false),
                    to_version = table.Column<string>(type: "text", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_kind = table.Column<string>(type: "text", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: false),
                    identified_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    review_outcome = table.Column<string>(type: "text", nullable: true),
                    review_reason = table.Column<string>(type: "text", nullable: true),
                    reviewed_by = table.Column<string>(type: "text", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pack_rollback_exception", x => x.exception_id);
                    table.CheckConstraint("ck_pack_rollback_exception_outcome", "review_outcome IS NULL OR review_outcome IN ('NO_ACTION', 'CORRECTION_REQUIRED')");
                    table.CheckConstraint("ck_pack_rollback_exception_review", "(status = 'OPEN') = (review_outcome IS NULL)");
                    table.CheckConstraint("ck_pack_rollback_exception_status", "status IN ('OPEN', 'REVIEWED')");
                    table.ForeignKey(
                        name: "fk_pack_rollback_exception_transaction",
                        column: x => x.transaction_id,
                        principalSchema: "pol",
                        principalTable: "policy_transaction",
                        principalColumn: "transaction_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_quote_version_stale_reason",
                schema: "pol",
                table: "quote_version",
                sql: "stale_reason IS NULL OR stale_reason = 'PACK_ROLLBACK'");

            migrationBuilder.CreateIndex(
                name: "ix_pack_rollback_exception_queue",
                schema: "pol",
                table: "pack_rollback_exception",
                columns: new[] { "legal_entity_id", "status", "identified_at" });

            migrationBuilder.CreateIndex(
                name: "IX_pack_rollback_exception_transaction_id",
                schema: "pol",
                table: "pack_rollback_exception",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "ux_pack_rollback_exception_activation_tx",
                schema: "pol",
                table: "pack_rollback_exception",
                columns: new[] { "activation_id", "transaction_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pack_rollback_exception",
                schema: "pol");

            migrationBuilder.DropCheckConstraint(
                name: "ck_quote_version_stale_reason",
                schema: "pol",
                table: "quote_version");

            migrationBuilder.DropColumn(
                name: "stale_reason",
                schema: "pol",
                table: "quote_version");
        }
    }
}
