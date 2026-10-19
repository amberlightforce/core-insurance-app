using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Withdrawal : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "withdrawal",
                schema: "pol",
                columns: table => new
                {
                    withdrawal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_ref = table.Column<string>(type: "text", nullable: false),
                    right = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawal", x => x.withdrawal_id);
                    table.CheckConstraint("ck_withdrawal_right", "\"right\" IN ('WITHDRAWAL', 'OBJECTION')");
                });

            migrationBuilder.CreateIndex(
                name: "ux_withdrawal_policy_channel_ref",
                schema: "pol",
                table: "withdrawal",
                columns: new[] { "policy_id", "channel_ref" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "withdrawal",
                schema: "pol");
        }
    }
}
