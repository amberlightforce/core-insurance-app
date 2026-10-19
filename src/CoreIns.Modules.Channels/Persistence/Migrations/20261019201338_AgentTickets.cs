using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Channels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket",
                schema: "chn",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool = table.Column<string>(type: "text", nullable: false),
                    arguments = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    interaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    executed_command_ref = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket", x => x.ticket_id);
                    table.CheckConstraint("ck_ticket_status", "status IN ('PENDING', 'APPROVED', 'REJECTED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_status",
                schema: "chn",
                table: "ticket",
                columns: new[] { "legal_entity_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket",
                schema: "chn");
        }
    }
}
