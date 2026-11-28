using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Underwriting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Declines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "decline",
                schema: "uw",
                columns: table => new
                {
                    decline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decline_number = table.Column<string>(type: "text", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    intake_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "text", nullable: false),
                    reason_codes = table.Column<string>(type: "jsonb", nullable: false),
                    pairs = table.Column<string>(type: "jsonb", nullable: false),
                    wording = table.Column<string>(type: "text", nullable: true),
                    automated = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    letter_data = table.Column<string>(type: "jsonb", nullable: false),
                    letter_status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    review_requested_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    review_requested_by = table.Column<string>(type: "text", nullable: true),
                    review_requester = table.Column<string>(type: "text", nullable: true),
                    review_statement = table.Column<string>(type: "text", nullable: true),
                    review_decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    review_decided_by = table.Column<string>(type: "text", nullable: true),
                    review_outcome = table.Column<string>(type: "text", nullable: true),
                    review_reason = table.Column<string>(type: "text", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decline", x => x.decline_id);
                    table.CheckConstraint("ck_decline_scope", "scope IN ('FULL', 'PARTIAL')");
                    table.CheckConstraint("ck_decline_status", "status IN ('Issued', 'ReviewRequested', 'Upheld', 'Overturned')");
                    table.CheckConstraint("ck_decline_subject", "(job_id IS NULL) <> (intake_id IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_decline_register",
                schema: "uw",
                table: "decline",
                columns: new[] { "legal_entity_id", "created_at", "decline_id" });

            migrationBuilder.CreateIndex(
                name: "ux_decline_intake",
                schema: "uw",
                table: "decline",
                column: "intake_id",
                unique: true,
                filter: "intake_id IS NOT NULL AND status <> 'Overturned'");

            migrationBuilder.CreateIndex(
                name: "ux_decline_job",
                schema: "uw",
                table: "decline",
                column: "job_id",
                unique: true,
                filter: "job_id IS NOT NULL AND status <> 'Overturned'");

            migrationBuilder.CreateIndex(
                name: "ux_decline_number",
                schema: "uw",
                table: "decline",
                columns: new[] { "legal_entity_id", "decline_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "decline",
                schema: "uw");
        }
    }
}
