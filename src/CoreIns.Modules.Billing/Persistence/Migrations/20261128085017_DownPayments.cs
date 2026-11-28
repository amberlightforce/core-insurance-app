using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DownPayments : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "down_payment",
                schema: "bil",
                columns: table => new
                {
                    down_payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "text", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    payment_reference = table.Column<string>(type: "text", nullable: false),
                    cleared_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    cleared_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    cleared_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_down_payment", x => x.down_payment_id);
                    table.CheckConstraint("ck_down_payment_amount", "amount > 0 AND cleared_amount >= 0");
                    table.CheckConstraint("ck_down_payment_record_version", "record_version >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ux_down_payment_job",
                schema: "bil",
                table: "down_payment",
                columns: new[] { "legal_entity_id", "job_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_down_payment_reference",
                schema: "bil",
                table: "down_payment",
                column: "payment_reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "down_payment",
                schema: "bil");
        }
    }
}
