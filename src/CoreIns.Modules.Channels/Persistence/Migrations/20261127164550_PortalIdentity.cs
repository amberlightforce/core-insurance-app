using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Channels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PortalIdentity : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portal_identity",
                schema: "chn",
                columns: table => new
                {
                    portal_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    verification = table.Column<string>(type: "text", nullable: false),
                    linked_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    linked_by = table.Column<string>(type: "text", nullable: false),
                    unlinked_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    unlinked_by = table.Column<string>(type: "text", nullable: true),
                    unlink_reason = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portal_identity", x => x.portal_identity_id);
                    table.CheckConstraint("ck_portal_identity_status", "status IN ('LINKED', 'UNLINKED')");
                    table.CheckConstraint("ck_portal_identity_unlinked", "status = 'LINKED' OR (unlinked_at IS NOT NULL AND unlinked_by IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ux_portal_identity_party",
                schema: "chn",
                table: "portal_identity",
                columns: new[] { "legal_entity_id", "party_id" },
                unique: true,
                filter: "status = 'LINKED'");

            migrationBuilder.CreateIndex(
                name: "ux_portal_identity_subject",
                schema: "chn",
                table: "portal_identity",
                columns: new[] { "legal_entity_id", "subject" },
                unique: true,
                filter: "status = 'LINKED'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portal_identity",
                schema: "chn");
        }
    }
}
