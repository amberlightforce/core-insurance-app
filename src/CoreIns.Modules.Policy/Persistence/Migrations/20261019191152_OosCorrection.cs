using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Policy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OosCorrection : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oos_correction",
                schema: "pol",
                columns: table => new
                {
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    original_refs = table.Column<string>(type: "jsonb", nullable: false),
                    correction_of = table.Column<Guid>(type: "uuid", nullable: false),
                    booking_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period_locked = table.Column<bool>(type: "boolean", nullable: false),
                    net_premium = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oos_correction", x => x.set_id);
                    table.CheckConstraint("ck_oos_correction_mode", "mode IN ('NET', 'GROSS')");
                    table.CheckConstraint("ck_oos_correction_status", "status IN ('APPLIED')");
                    table.ForeignKey(
                        name: "fk_oos_correction_transaction",
                        column: x => x.transaction_id,
                        principalSchema: "pol",
                        principalTable: "policy_transaction",
                        principalColumn: "transaction_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oos_correction_policy",
                schema: "pol",
                table: "oos_correction",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ux_oos_correction_transaction",
                schema: "pol",
                table: "oos_correction",
                column: "transaction_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oos_correction",
                schema: "pol");
        }
    }
}
