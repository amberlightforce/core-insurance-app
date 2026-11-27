using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FnolDraft : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fnol_draft",
                schema: "clm",
                columns: table => new
                {
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    payload_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fnol_draft", x => x.draft_id);
                    table.CheckConstraint("ck_fnol_draft_claim_shape", "(status = 'SUBMITTED') = (claim_id IS NOT NULL)");
                    table.CheckConstraint("ck_fnol_draft_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_fnol_draft_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_fnol_draft_status", "status IN ('DRAFT', 'SUBMITTED', 'DISCARDED')");
                    table.ForeignKey(
                        name: "fk_fnol_draft_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fnol_draft_claim_id",
                schema: "clm",
                table: "fnol_draft",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_fnol_draft_owner",
                schema: "clm",
                table: "fnol_draft",
                columns: new[] { "legal_entity_id", "created_by", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fnol_draft",
                schema: "clm");
        }
    }
}
