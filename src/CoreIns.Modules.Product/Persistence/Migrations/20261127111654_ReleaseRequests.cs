using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Product.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseRequests : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "release_request",
                schema: "pfc",
                columns: table => new
                {
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    new_major = table.Column<int>(type: "integer", nullable: false),
                    new_minor = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "char(64)", nullable: false),
                    candidate_artefact_hash = table.Column<string>(type: "char(64)", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_by = table.Column<string>(type: "text", nullable: false),
                    submitted_by_principal = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_comment = table.Column<string>(type: "text", nullable: true),
                    activation_date = table.Column<DateOnly>(type: "date", nullable: true),
                    new_business_from = table.Column<DateOnly>(type: "date", nullable: true),
                    new_business_to = table.Column<DateOnly>(type: "date", nullable: true),
                    renewal_from = table.Column<DateOnly>(type: "date", nullable: true),
                    renewal_to = table.Column<DateOnly>(type: "date", nullable: true),
                    scheduled_by = table.Column<string>(type: "text", nullable: true),
                    scheduled_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    schedule_reason = table.Column<string>(type: "text", nullable: true),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_release_request", x => x.release_id);
                    table.CheckConstraint("ck_release_hash", "payload_hash ~ '^[0-9a-f]{64}$' AND candidate_artefact_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_release_new_number", "new_major >= 0 AND new_minor >= 0");
                    table.CheckConstraint("ck_release_published", "(status = 'PUBLISHED') = (published_version_id IS NOT NULL)");
                    table.CheckConstraint("ck_release_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_release_scheduled", "(status IN ('SCHEDULED', 'PUBLISHED')) = (new_business_from IS NOT NULL AND renewal_from IS NOT NULL AND activation_date IS NOT NULL)");
                    table.CheckConstraint("ck_release_sod", "decided_by IS NULL OR decided_by <> submitted_by");
                    table.CheckConstraint("ck_release_status", "status IN ('SUBMITTED', 'APPROVED', 'RETURNED', 'SCHEDULED', 'PUBLISHED')");
                    table.ForeignKey(
                        name: "fk_release_change_set",
                        column: x => x.change_set_id,
                        principalSchema: "pfc",
                        principalTable: "change_set",
                        principalColumn: "change_set_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_release_product",
                        column: x => x.product_id,
                        principalSchema: "pfc",
                        principalTable: "product",
                        principalColumn: "product_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_release_published_version",
                        column: x => x.published_version_id,
                        principalSchema: "pfc",
                        principalTable: "product_version",
                        principalColumn: "product_version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_release_due",
                schema: "pfc",
                table: "release_request",
                columns: new[] { "status", "activation_date" });

            migrationBuilder.CreateIndex(
                name: "IX_release_request_published_version_id",
                schema: "pfc",
                table: "release_request",
                column: "published_version_id");

            migrationBuilder.CreateIndex(
                name: "ux_release_approval",
                schema: "pfc",
                table: "release_request",
                column: "approval_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_release_live_per_change_set",
                schema: "pfc",
                table: "release_request",
                column: "change_set_id",
                unique: true,
                filter: "status IN ('SUBMITTED', 'APPROVED', 'SCHEDULED')");

            migrationBuilder.CreateIndex(
                name: "ux_release_live_per_number",
                schema: "pfc",
                table: "release_request",
                columns: new[] { "product_id", "new_major", "new_minor" },
                unique: true,
                filter: "status IN ('SUBMITTED', 'APPROVED', 'SCHEDULED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "release_request",
                schema: "pfc");
        }
    }
}
