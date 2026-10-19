using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Channels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialChannelsWithdrawal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "chn");

            migrationBuilder.CreateTable(
                name: "withdrawal_request",
                schema: "chn",
                columns: table => new
                {
                    withdrawal_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_no = table.Column<string>(type: "text", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    declarant_name = table.Column<string>(type: "text", nullable: false),
                    declarant_email = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    channel = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    declared_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    declared_by = table.Column<string>(type: "text", nullable: false),
                    concluded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    window_days = table.Column<int>(type: "integer", nullable: false),
                    deadline_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: true),
                    token_expires_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decline_code = table.Column<string>(type: "text", nullable: true),
                    channel_ref = table.Column<string>(type: "text", nullable: false),
                    pol_submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawal_request", x => x.withdrawal_request_id);
                    table.CheckConstraint("ck_withdrawal_request_confirmed", "status IN ('DECLARED', 'DECLINED') OR confirmed_at IS NOT NULL");
                    table.CheckConstraint("ck_withdrawal_request_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_withdrawal_request_status", "status IN ('DECLARED', 'CONFIRMED', 'SUBMITTED', 'PROCESSED', 'REJECTED', 'DECLINED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawal_request_policy",
                schema: "chn",
                table: "withdrawal_request",
                columns: new[] { "legal_entity_id", "policy_id" });

            migrationBuilder.CreateIndex(
                name: "ux_withdrawal_request_channel_ref",
                schema: "chn",
                table: "withdrawal_request",
                column: "channel_ref",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_withdrawal_request_no",
                schema: "chn",
                table: "withdrawal_request",
                column: "request_no",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "withdrawal_request",
                schema: "chn");
        }
    }
}
