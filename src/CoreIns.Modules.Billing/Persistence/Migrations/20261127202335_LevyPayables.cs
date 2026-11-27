using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LevyPayables : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "levy_payable",
                schema: "bil",
                columns: table => new
                {
                    levy_payable_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    levy_authority = table.Column<string>(type: "text", nullable: false),
                    period = table.Column<string>(type: "char(7)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    collected_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    payable_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    legal_status = table.Column<string>(type: "text", nullable: false),
                    provisional = table.Column<bool>(type: "boolean", nullable: false),
                    payee_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payee_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    disbursement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_levy_payable", x => x.levy_payable_id);
                    table.CheckConstraint("ck_levy_payable_amount", "payable_amount > 0 AND collected_amount >= payable_amount");
                    table.CheckConstraint("ck_levy_payable_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_levy_payable_period", "period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                    table.CheckConstraint("ck_levy_payable_record_version", "record_version >= 1");
                    table.ForeignKey(
                        name: "fk_levy_payable_disbursement",
                        column: x => x.disbursement_id,
                        principalSchema: "bil",
                        principalTable: "disbursement",
                        principalColumn: "disbursement_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_levy_payable_disbursement",
                schema: "bil",
                table: "levy_payable",
                column: "disbursement_id");

            migrationBuilder.CreateIndex(
                name: "ux_levy_payable_authority_period",
                schema: "bil",
                table: "levy_payable",
                columns: new[] { "legal_entity_id", "levy_authority", "period" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "levy_payable",
                schema: "bil");
        }
    }
}
