using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgencyAdmin : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agency_user",
                schema: "pty",
                columns: table => new
                {
                    agency_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intermediary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_subject = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agency_user", x => x.agency_user_id);
                    table.CheckConstraint("ck_agency_user_role", "role IN ('AGENCY_ADMIN','OFFICE_MANAGER','PRODUCER','AGENCY_STAFF')");
                    table.CheckConstraint("ck_agency_user_status", "status IN ('ACTIVE','SUSPENDED','REMOVED')");
                    table.ForeignKey(
                        name: "fk_agency_user_intermediary",
                        column: x => x.intermediary_id,
                        principalSchema: "pty",
                        principalTable: "intermediary",
                        principalColumn: "intermediary_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agency_grant",
                schema: "pty",
                columns: table => new
                {
                    grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intermediary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    producer_code = table.Column<string>(type: "text", nullable: false),
                    granted_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    granted_by = table.Column<string>(type: "text", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    revoked_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agency_grant", x => x.grant_id);
                    table.ForeignKey(
                        name: "fk_agency_grant_user",
                        column: x => x.agency_user_id,
                        principalSchema: "pty",
                        principalTable: "agency_user",
                        principalColumn: "agency_user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agency_grant_code",
                schema: "pty",
                table: "agency_grant",
                columns: new[] { "legal_entity_id", "producer_code" });

            migrationBuilder.CreateIndex(
                name: "ux_agency_grant_open",
                schema: "pty",
                table: "agency_grant",
                columns: new[] { "agency_user_id", "producer_code" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_agency_user_agency",
                schema: "pty",
                table: "agency_user",
                columns: new[] { "intermediary_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_agency_user_subject",
                schema: "pty",
                table: "agency_user",
                columns: new[] { "legal_entity_id", "external_subject" },
                unique: true,
                filter: "status <> 'REMOVED'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agency_grant",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "agency_user",
                schema: "pty");
        }
    }
}
