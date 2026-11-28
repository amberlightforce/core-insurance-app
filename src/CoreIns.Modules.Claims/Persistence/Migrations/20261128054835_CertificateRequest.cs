using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CertificateRequest : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certificate_request",
                schema: "clm",
                columns: table => new
                {
                    certificate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_numbers = table.Column<string[]>(type: "text[]", nullable: false),
                    period_from = table.Column<DateOnly>(type: "date", nullable: false),
                    period_to = table.Column<DateOnly>(type: "date", nullable: false),
                    lookback_years = table.Column<int>(type: "integer", nullable: false),
                    legal_status = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    claims = table.Column<string>(type: "jsonb", nullable: false),
                    certificate_number = table.Column<string>(type: "text", nullable: true),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_request", x => x.certificate_id);
                    table.CheckConstraint("ck_certificate_request_issued", "status <> 'ISSUED' OR (certificate_number IS NOT NULL AND document_id IS NOT NULL AND issued_at IS NOT NULL)");
                    table.CheckConstraint("ck_certificate_request_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_certificate_request_period", "period_to >= period_from");
                    table.CheckConstraint("ck_certificate_request_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_certificate_request_status", "status IN ('REQUESTED', 'ASSEMBLED', 'ISSUED')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_request_requester",
                schema: "clm",
                table: "certificate_request",
                columns: new[] { "legal_entity_id", "requester_party_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_certificate_request_number",
                schema: "clm",
                table: "certificate_request",
                columns: new[] { "legal_entity_id", "certificate_number" },
                unique: true,
                filter: "certificate_number IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certificate_request",
                schema: "clm");
        }
    }
}
