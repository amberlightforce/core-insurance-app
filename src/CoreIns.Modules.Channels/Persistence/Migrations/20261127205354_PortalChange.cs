using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Channels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PortalChange : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portal_change",
                schema: "chn",
                columns: table => new
                {
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    effective_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    payment_plan_option = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portal_change", x => x.change_id);
                    table.CheckConstraint("ck_portal_change_amount", "amount >= 0");
                    table.CheckConstraint("ck_portal_change_status", "status IN ('PREVIEWED', 'CONFIRMED', 'REFERRED')");
                    table.CheckConstraint("ck_portal_change_version", "version_no >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_portal_change_party",
                schema: "chn",
                table: "portal_change",
                columns: new[] { "legal_entity_id", "party_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_portal_change_job",
                schema: "chn",
                table: "portal_change",
                columns: new[] { "legal_entity_id", "job_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portal_change",
                schema: "chn");
        }
    }
}
