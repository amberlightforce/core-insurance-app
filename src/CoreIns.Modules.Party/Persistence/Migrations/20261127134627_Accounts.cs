using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Accounts : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account",
                schema: "pty",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "text", nullable: false),
                    account_type = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    holder_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    withdrawn_on = table.Column<DateOnly>(type: "date", nullable: true),
                    withdraw_reason = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account", x => x.account_id);
                    table.CheckConstraint("ck_account_status", "status IN ('PENDING','ACTIVE','WITHDRAWN')");
                    table.CheckConstraint("ck_account_type", "account_type IN ('INDIVIDUAL','HOUSEHOLD','BUSINESS')");
                    table.ForeignKey(
                        name: "fk_account_holder",
                        column: x => x.holder_party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_member",
                schema: "pty",
                columns: table => new
                {
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_role = table.Column<string>(type: "text", nullable: false),
                    added_on = table.Column<DateOnly>(type: "date", nullable: false),
                    added_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    removed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_member", x => x.member_id);
                    table.ForeignKey(
                        name: "fk_account_member_account",
                        column: x => x.account_id,
                        principalSchema: "pty",
                        principalTable: "account",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_account_member_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_holder",
                schema: "pty",
                table: "account",
                column: "holder_party_id");

            migrationBuilder.CreateIndex(
                name: "ux_account_number",
                schema: "pty",
                table: "account",
                columns: new[] { "legal_entity_id", "account_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_account_member_account",
                schema: "pty",
                table: "account_member",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_member_party",
                schema: "pty",
                table: "account_member",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "ux_account_member_current",
                schema: "pty",
                table: "account_member",
                columns: new[] { "account_id", "party_id" },
                unique: true,
                filter: "removed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_member",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "account",
                schema: "pty");
        }
    }
}
