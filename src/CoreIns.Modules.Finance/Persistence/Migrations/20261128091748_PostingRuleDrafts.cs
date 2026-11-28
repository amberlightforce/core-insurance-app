using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PostingRuleDrafts : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "posting_rule_draft",
                schema: "fin",
                columns: table => new
                {
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_code = table.Column<string>(type: "text", nullable: false),
                    book = table.Column<string>(type: "text", nullable: false),
                    base_version = table.Column<int>(type: "integer", nullable: true),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false),
                    sample_events = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    submitted_by = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_reason = table.Column<string>(type: "text", nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_rule_set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_version = table.Column<int>(type: "integer", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posting_rule_draft", x => x.draft_id);
                    table.CheckConstraint("ck_posting_rule_draft_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_posting_rule_draft_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_posting_rule_draft_status", "status IN ('DRAFT', 'SUBMITTED', 'APPROVED', 'REJECTED', 'WITHDRAWN')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_posting_rule_draft_book",
                schema: "fin",
                table: "posting_rule_draft",
                columns: new[] { "legal_entity_id", "book", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "posting_rule_draft",
                schema: "fin");
        }
    }
}
