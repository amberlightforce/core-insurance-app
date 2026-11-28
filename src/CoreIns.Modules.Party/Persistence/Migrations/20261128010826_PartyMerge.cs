using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartyMerge : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "party_merge",
                schema: "pty",
                columns: table => new
                {
                    merge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    survivor_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merged_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    prior_status = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_comment = table.Column<string>(type: "text", nullable: true),
                    executed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    unmerge_window_end = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    unmerged_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    unmerged_by = table.Column<string>(type: "text", nullable: true),
                    unmerge_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party_merge", x => x.merge_id);
                    table.CheckConstraint("ck_party_merge_distinct", "survivor_party_id <> merged_party_id");
                    table.CheckConstraint("ck_party_merge_status", "status IN ('PENDING_APPROVAL','EXECUTED','REJECTED','UNMERGED')");
                    table.ForeignKey(
                        name: "fk_party_merge_merged",
                        column: x => x.merged_party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_party_merge_survivor",
                        column: x => x.survivor_party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_party_merge_status",
                schema: "pty",
                table: "party_merge",
                columns: new[] { "legal_entity_id", "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_party_merge_survivor",
                schema: "pty",
                table: "party_merge",
                column: "survivor_party_id");

            migrationBuilder.CreateIndex(
                name: "ux_party_merge_open_merged",
                schema: "pty",
                table: "party_merge",
                column: "merged_party_id",
                unique: true,
                filter: "status IN ('PENDING_APPROVAL','EXECUTED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_merge",
                schema: "pty");
        }
    }
}
