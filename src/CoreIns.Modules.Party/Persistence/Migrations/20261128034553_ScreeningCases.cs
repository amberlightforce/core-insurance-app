using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScreeningCases : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "screening_case",
                schema: "pty",
                columns: table => new
                {
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_number = table.Column<string>(type: "text", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subject_key = table.Column<string>(type: "text", nullable: false),
                    subject_name = table.Column<string>(type: "text", nullable: true),
                    country = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    match_fingerprint = table.Column<string>(type: "text", nullable: false),
                    evidence = table.Column<string>(type: "text", nullable: false),
                    list_versions = table.Column<string>(type: "text", nullable: false),
                    caller_module = table.Column<string>(type: "text", nullable: false),
                    caller_type = table.Column<string>(type: "text", nullable: false),
                    caller_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    proposed_decision = table.Column<string>(type: "text", nullable: true),
                    proposed_by = table.Column<string>(type: "text", nullable: true),
                    proposed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    proposal_comment = table.Column<string>(type: "text", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_comment = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_screening_case", x => x.case_id);
                    table.CheckConstraint("ck_screening_case_proposed", "proposed_decision IS NULL OR proposed_decision IN ('CLEAR','TRUE_MATCH')");
                    table.CheckConstraint("ck_screening_case_status", "status IN ('OPEN','PENDING_APPROVAL','CLEARED','TRUE_MATCH')");
                    table.ForeignKey(
                        name: "fk_screening_case_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_screening_case_party_id",
                schema: "pty",
                table: "screening_case",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "ix_screening_case_status",
                schema: "pty",
                table: "screening_case",
                columns: new[] { "legal_entity_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_screening_case_subject",
                schema: "pty",
                table: "screening_case",
                columns: new[] { "legal_entity_id", "subject_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_screening_case_number",
                schema: "pty",
                table: "screening_case",
                column: "case_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "screening_case",
                schema: "pty");
        }
    }
}
