using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Respread : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "correction_of",
                schema: "bil",
                table: "charge",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "oos_set_id",
                schema: "bil",
                table: "charge",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "respread",
                schema: "bil",
                columns: table => new
                {
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    oos_set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correction_of = table.Column<Guid>(type: "uuid", nullable: true),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    invoiced_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    credited_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    paid_invoices_untouched = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_respread", x => x.set_id);
                    table.CheckConstraint("ck_respread_net", "net_amount = invoiced_amount - credited_amount");
                });

            migrationBuilder.CreateIndex(
                name: "ix_respread_oos_set",
                schema: "bil",
                table: "respread",
                column: "oos_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_respread_term",
                schema: "bil",
                table: "respread",
                column: "term_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "respread",
                schema: "bil");

            migrationBuilder.DropColumn(
                name: "correction_of",
                schema: "bil",
                table: "charge");

            migrationBuilder.DropColumn(
                name: "oos_set_id",
                schema: "bil",
                table: "charge");
        }
    }
}
