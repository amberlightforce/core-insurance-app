using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "doc");

            migrationBuilder.CreateTable(
                name: "archive",
                schema: "doc",
                columns: table => new
                {
                    archive_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive", x => x.archive_id);
                    table.CheckConstraint("ck_archive_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "delivery",
                schema: "doc",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_role = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    medium = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    proof_level = table.Column<string>(type: "text", nullable: true),
                    notification_date = table.Column<DateOnly>(type: "date", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    is_reprint = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery", x => x.delivery_id);
                    table.CheckConstraint("ck_delivery_delivered_date", "status <> 'Delivered' OR notification_date IS NOT NULL");
                    table.CheckConstraint("ck_delivery_status", "status IN ('Pending','Sent','Delivered','Failed','Bounced')");
                });

            migrationBuilder.CreateTable(
                name: "document",
                schema: "doc",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_number = table.Column<string>(type: "text", nullable: false),
                    template_key = table.Column<string>(type: "text", nullable: false),
                    request_no = table.Column<string>(type: "text", nullable: false),
                    subject_module = table.Column<string>(type: "text", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    recipient_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "char(2)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    medium = table.Column<string>(type: "text", nullable: false),
                    archive_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    rendered_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document", x => x.document_id);
                    table.CheckConstraint("ck_document_status", "status IN ('Requested','Rendering','Rendered','Failed','Superseded')");
                });

            migrationBuilder.CreateTable(
                name: "evidence",
                schema: "doc",
                columns: table => new
                {
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_event_id = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    proof_date = table.Column<DateOnly>(type: "date", nullable: true),
                    proof_reference = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence", x => x.evidence_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_document",
                schema: "doc",
                table: "delivery",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_subject",
                schema: "doc",
                table: "document",
                columns: new[] { "subject_module", "subject_type", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ux_document_number",
                schema: "doc",
                table: "document",
                columns: new[] { "legal_entity_id", "document_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_request",
                schema: "doc",
                table: "document",
                columns: new[] { "legal_entity_id", "template_key", "request_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_evidence_document",
                schema: "doc",
                table: "evidence",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ux_evidence_provider_event",
                schema: "doc",
                table: "evidence",
                columns: new[] { "provider", "provider_event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archive",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "delivery",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "document",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "evidence",
                schema: "doc");
        }
    }
}
