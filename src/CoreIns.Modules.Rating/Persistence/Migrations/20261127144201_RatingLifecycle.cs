using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Rating.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RatingLifecycle : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_set",
                schema: "rat",
                columns: table => new
                {
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: false),
                    base_artefact_hash = table.Column<string>(type: "char(64)", nullable: true),
                    tables = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_set", x => x.change_set_id);
                    table.CheckConstraint("ck_change_set_status", "status IN ('OPEN', 'BUILT')");
                });

            migrationBuilder.CreateTable(
                name: "artifact_release",
                schema: "rat",
                columns: table => new
                {
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artefact_hash = table.Column<string>(type: "char(64)", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: false),
                    data_status = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "char(64)", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    built_by = table.Column<string>(type: "text", nullable: false),
                    built_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    submitted_by = table.Column<string>(type: "text", nullable: true),
                    submitted_by_principal = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_comment = table.Column<string>(type: "text", nullable: true),
                    activation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifact_release", x => x.release_id);
                    table.CheckConstraint("ck_artifact_release_status", "status IN ('BUILT', 'SUBMITTED', 'APPROVED', 'RETURNED', 'SCHEDULED', 'ROLLED_BACK')");
                    table.ForeignKey(
                        name: "fk_artifact_release_artifact",
                        column: x => x.artefact_hash,
                        principalSchema: "rat",
                        principalTable: "rating_artifact",
                        principalColumn: "artefact_hash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_artifact_release_change_set",
                        column: x => x.change_set_id,
                        principalSchema: "rat",
                        principalTable: "change_set",
                        principalColumn: "change_set_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_release_change_set_id",
                schema: "rat",
                table: "artifact_release",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "ux_artifact_release_artifact",
                schema: "rat",
                table: "artifact_release",
                column: "artefact_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_change_set_product",
                schema: "rat",
                table: "change_set",
                columns: new[] { "product_code", "product_version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact_release",
                schema: "rat");

            migrationBuilder.DropTable(
                name: "change_set",
                schema: "rat");
        }
    }
}
