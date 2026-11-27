using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNonRenewal : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "non_renewal",
                schema: "pol",
                columns: table => new
                {
                    non_renewal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    effective_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    notice_deadline = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    notice_days = table.Column<int>(type: "integer", nullable: false),
                    legal_status = table.Column<string>(type: "text", nullable: false),
                    notice_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_non_renewal", x => x.non_renewal_id);
                    table.CheckConstraint("ck_non_renewal_status", "status IN ('NOTICE_REQUESTED', 'PSR_REVIEW')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_non_renewal_policy",
                schema: "pol",
                table: "non_renewal",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ux_non_renewal_term",
                schema: "pol",
                table: "non_renewal",
                column: "term_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "non_renewal",
                schema: "pol");
        }
    }
}
