using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommissionAgreements : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "commission_agreement",
                schema: "pty",
                columns: table => new
                {
                    agreement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intermediary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: true),
                    line_of_business = table.Column<string>(type: "text", nullable: true),
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_agreement", x => x.agreement_id);
                    table.ForeignKey(
                        name: "fk_commission_agreement_intermediary",
                        column: x => x.intermediary_id,
                        principalSchema: "pty",
                        principalTable: "intermediary",
                        principalColumn: "intermediary_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commission_agreement_version",
                schema: "pty",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agreement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    expected_volume = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    max_rate_pct = table.Column<decimal>(type: "numeric(7,4)", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    submitted_by = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_comment = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_agreement_version", x => x.version_id);
                    table.CheckConstraint("ck_commission_version_rate", "max_rate_pct >= 0 AND max_rate_pct <= 100");
                    table.CheckConstraint("ck_commission_version_status", "status IN ('DRAFT','PENDING_APPROVAL','ACTIVE','REJECTED')");
                    table.ForeignKey(
                        name: "fk_commission_version_agreement",
                        column: x => x.agreement_id,
                        principalSchema: "pty",
                        principalTable: "commission_agreement",
                        principalColumn: "agreement_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commission_rate_line",
                schema: "pty",
                columns: table => new
                {
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    business_type = table.Column<string>(type: "text", nullable: false),
                    line_of_business = table.Column<string>(type: "text", nullable: true),
                    basis = table.Column<string>(type: "text", nullable: false),
                    rate_pct = table.Column<decimal>(type: "numeric(7,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_rate_line", x => x.line_id);
                    table.CheckConstraint("ck_commission_line_rate", "rate_pct >= 0 AND rate_pct <= 100");
                    table.ForeignKey(
                        name: "fk_commission_line_version",
                        column: x => x.version_id,
                        principalSchema: "pty",
                        principalTable: "commission_agreement_version",
                        principalColumn: "version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_commission_agreement_intermediary_id",
                schema: "pty",
                table: "commission_agreement",
                column: "intermediary_id");

            migrationBuilder.CreateIndex(
                name: "ux_commission_agreement_scope",
                schema: "pty",
                table: "commission_agreement",
                columns: new[] { "legal_entity_id", "intermediary_id", "scope_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_commission_version_no",
                schema: "pty",
                table: "commission_agreement_version",
                columns: new[] { "agreement_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_commission_version_open",
                schema: "pty",
                table: "commission_agreement_version",
                column: "agreement_id",
                unique: true,
                filter: "status IN ('DRAFT','PENDING_APPROVAL')");

            migrationBuilder.CreateIndex(
                name: "ux_commission_line_no",
                schema: "pty",
                table: "commission_rate_line",
                columns: new[] { "version_id", "line_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "commission_rate_line",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "commission_agreement_version",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "commission_agreement",
                schema: "pty");
        }
    }
}
