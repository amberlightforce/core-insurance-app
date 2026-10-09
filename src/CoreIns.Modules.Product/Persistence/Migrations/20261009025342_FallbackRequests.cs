using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Product.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FallbackRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "fallback_of_version_id",
                schema: "pfc",
                table: "product_version",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "replaces_version_id",
                schema: "pfc",
                table: "product_version",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fallback_request",
                schema: "pfc",
                columns: table => new
                {
                    fallback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    defective_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    new_major = table.Column<int>(type: "integer", nullable: false),
                    new_minor = table.Column<int>(type: "integer", nullable: false),
                    fallback_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "char(64)", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    requested_by_principal = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_reason = table.Column<string>(type: "text", nullable: true),
                    new_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fallback_request", x => x.fallback_id);
                    table.CheckConstraint("ck_fallback_applied", "(status = 'APPLIED') = (new_version_id IS NOT NULL)");
                    table.CheckConstraint("ck_fallback_decision", "(status = 'PENDING_APPROVAL') = (decided_at IS NULL AND decided_by IS NULL)");
                    table.CheckConstraint("ck_fallback_hash", "payload_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_fallback_new_number", "new_major >= 0 AND new_minor >= 0");
                    table.CheckConstraint("ck_fallback_reason", "char_length(reason) BETWEEN 20 AND 128");
                    table.CheckConstraint("ck_fallback_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_fallback_sod", "decided_by IS NULL OR decided_by <> requested_by");
                    table.CheckConstraint("ck_fallback_status", "status IN ('PENDING_APPROVAL', 'APPLIED', 'REJECTED')");
                    table.ForeignKey(
                        name: "fk_fallback_defective",
                        column: x => x.defective_version_id,
                        principalSchema: "pfc",
                        principalTable: "product_version",
                        principalColumn: "product_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fallback_new_version",
                        column: x => x.new_version_id,
                        principalSchema: "pfc",
                        principalTable: "product_version",
                        principalColumn: "product_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fallback_product",
                        column: x => x.product_id,
                        principalSchema: "pfc",
                        principalTable: "product",
                        principalColumn: "product_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fallback_source",
                        column: x => x.source_version_id,
                        principalSchema: "pfc",
                        principalTable: "product_version",
                        principalColumn: "product_version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_version_fallback_of_version_id",
                schema: "pfc",
                table: "product_version",
                column: "fallback_of_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_version_replaces_version_id",
                schema: "pfc",
                table: "product_version",
                column: "replaces_version_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_version_fallback_pair",
                schema: "pfc",
                table: "product_version",
                sql: "(fallback_of_version_id IS NULL) = (replaces_version_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_fallback_request_new_version_id",
                schema: "pfc",
                table: "fallback_request",
                column: "new_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_fallback_request_product_id",
                schema: "pfc",
                table: "fallback_request",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_fallback_request_source_version_id",
                schema: "pfc",
                table: "fallback_request",
                column: "source_version_id");

            migrationBuilder.CreateIndex(
                name: "ux_fallback_approval",
                schema: "pfc",
                table: "fallback_request",
                column: "approval_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_fallback_live_per_defective",
                schema: "pfc",
                table: "fallback_request",
                column: "defective_version_id",
                unique: true,
                filter: "status <> 'REJECTED'");

            migrationBuilder.AddForeignKey(
                name: "fk_product_version_fallback_of",
                schema: "pfc",
                table: "product_version",
                column: "fallback_of_version_id",
                principalSchema: "pfc",
                principalTable: "product_version",
                principalColumn: "product_version_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_product_version_replaces",
                schema: "pfc",
                table: "product_version",
                column: "replaces_version_id",
                principalSchema: "pfc",
                principalTable: "product_version",
                principalColumn: "product_version_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_product_version_fallback_of",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropForeignKey(
                name: "fk_product_version_replaces",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropTable(
                name: "fallback_request",
                schema: "pfc");

            migrationBuilder.DropIndex(
                name: "IX_product_version_fallback_of_version_id",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropIndex(
                name: "IX_product_version_replaces_version_id",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_version_fallback_pair",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropColumn(
                name: "fallback_of_version_id",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropColumn(
                name: "replaces_version_id",
                schema: "pfc",
                table: "product_version");
        }
    }
}
