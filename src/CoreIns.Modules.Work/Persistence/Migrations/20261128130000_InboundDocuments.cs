using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InboundDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbound_document",
                schema: "wrk",
                columns: table => new
                {
                    inbound_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inbound_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    archive_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sender = table.Column<string>(type: "text", nullable: false),
                    envelope_metadata = table.Column<string>(type: "text", nullable: false),
                    suggested_class = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    document_class = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    link_object = table.Column<string>(type: "character varying(340)", maxLength: 340, nullable: true),
                    keyed_data = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scan_verdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    scan_signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    scanner = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    scanned_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    page_range = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reject_reason = table.Column<string>(type: "text", nullable: true),
                    redacted_archive_id = table.Column<Guid>(type: "uuid", nullable: true),
                    redaction_reason = table.Column<string>(type: "text", nullable: true),
                    legal_hold = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    verified_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    verified_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_document", x => x.inbound_document_id);
                    table.CheckConstraint("ck_inbound_clean_scanned", "state NOT IN ('CLEAN', 'VERIFIED', 'SPLIT') OR scan_verdict = 'CLEAN'");
                    table.CheckConstraint("ck_inbound_hold", "redacted_archive_id IS NULL OR (legal_hold AND archive_id IS NOT NULL)");
                    table.CheckConstraint("ck_inbound_infected", "scan_verdict IS DISTINCT FROM 'INFECTED' OR (state IN ('INFECTED', 'REJECTED') AND archive_id IS NULL)");
                    table.CheckConstraint("ck_inbound_state", "state IN ('QUARANTINED', 'CLEAN', 'INFECTED', 'VERIFIED', 'REJECTED', 'SPLIT')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_document_envelope",
                schema: "wrk",
                table: "inbound_document",
                column: "envelope_id");

            migrationBuilder.CreateIndex(
                name: "ix_inbound_document_link",
                schema: "wrk",
                table: "inbound_document",
                columns: new[] { "legal_entity_id", "link_object" });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_document_queue",
                schema: "wrk",
                table: "inbound_document",
                columns: new[] { "legal_entity_id", "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_inbound_document_number",
                schema: "wrk",
                table: "inbound_document",
                column: "inbound_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbound_document",
                schema: "wrk");
        }
    }
}
