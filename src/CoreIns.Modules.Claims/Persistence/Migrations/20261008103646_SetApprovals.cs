using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SetApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "set_approval",
                schema: "clm",
                columns: table => new
                {
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_type = table.Column<string>(type: "text", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "char(64)", nullable: false),
                    authority_type = table.Column<string>(type: "text", nullable: false),
                    authority_cost_type = table.Column<string>(type: "text", nullable: false),
                    authority_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    checker = table.Column<string>(type: "text", nullable: true),
                    checker_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_set_approval", x => x.approval_request_id);
                    table.CheckConstraint("ck_set_approval_amount", "authority_amount > 0");
                    table.CheckConstraint("ck_set_approval_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_set_approval_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_set_approval_status", "status IN ('PENDING', 'APPROVED', 'REJECTED')");
                    table.ForeignKey(
                        name: "fk_set_approval_set",
                        column: x => x.set_id,
                        principalSchema: "clm",
                        principalTable: "transaction_set",
                        principalColumn: "set_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_set_approval_bucket",
                schema: "clm",
                table: "set_approval",
                columns: new[] { "set_id", "authority_type", "authority_cost_type" },
                unique: true);

            migrationBuilder.Sql(ClaimsFinancialSql.SetApprovalsUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ClaimsFinancialSql.SetApprovalsDown);

            migrationBuilder.DropTable(
                name: "set_approval",
                schema: "clm");
        }
    }
}
