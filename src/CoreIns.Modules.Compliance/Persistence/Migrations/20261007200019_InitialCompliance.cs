using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cmp");

            migrationBuilder.CreateTable(
                name: "fiscal_document",
                schema: "cmp",
                columns: table => new
                {
                    fiscal_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    document_type = table.Column<string>(type: "text", nullable: false),
                    document_type_is_placeholder = table.Column<bool>(type: "boolean", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    counterparty_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    total = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    series = table.Column<string>(type: "text", nullable: true),
                    number = table.Column<string>(type: "text", nullable: true),
                    mark = table.Column<string>(type: "text", nullable: true),
                    uid = table.Column<string>(type: "text", nullable: true),
                    qr_payload_ref = table.Column<string>(type: "text", nullable: true),
                    channel = table.Column<string>(type: "text", nullable: false),
                    stub = table.Column<bool>(type: "boolean", nullable: false),
                    rejection_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    registered_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fiscal_document", x => x.fiscal_document_id);
                    table.CheckConstraint("ck_fiscal_document_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_fiscal_document_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_fiscal_document_registered", "status <> 'REGISTERED' OR (mark IS NOT NULL AND uid IS NOT NULL)");
                    table.CheckConstraint("ck_fiscal_document_revision", "revision >= 0");
                    table.CheckConstraint("ck_fiscal_document_role", "role IN ('ISSUE', 'CREDIT', 'CANCELLATION')");
                    table.CheckConstraint("ck_fiscal_document_status", "status IN ('PENDING', 'REGISTERED', 'REJECTED')");
                });

            migrationBuilder.CreateTable(
                name: "fiscal_series",
                schema: "cmp",
                columns: table => new
                {
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<string>(type: "text", nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fiscal_series", x => new { x.legal_entity_id, x.series_id });
                    table.CheckConstraint("ck_fiscal_series_last", "last_number >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_document_number",
                schema: "cmp",
                table: "fiscal_document",
                columns: new[] { "legal_entity_id", "series", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_document_source",
                schema: "cmp",
                table: "fiscal_document",
                columns: new[] { "legal_entity_id", "source_type", "source_id", "role", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fiscal_document",
                schema: "cmp");

            migrationBuilder.DropTable(
                name: "fiscal_series",
                schema: "cmp");
        }
    }
}
