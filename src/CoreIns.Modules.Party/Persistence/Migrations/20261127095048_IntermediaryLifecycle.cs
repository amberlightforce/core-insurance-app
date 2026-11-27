using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntermediaryLifecycle : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_intermediary_id",
                schema: "pty",
                table: "intermediary",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "intermediary_appointment",
                schema: "pty",
                columns: table => new
                {
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intermediary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intermediary_appointment", x => x.appointment_id);
                    table.ForeignKey(
                        name: "fk_intermediary_appointment_intermediary",
                        column: x => x.intermediary_id,
                        principalSchema: "pty",
                        principalTable: "intermediary",
                        principalColumn: "intermediary_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "intermediary_conflict_of_interest",
                schema: "pty",
                columns: table => new
                {
                    conflict_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intermediary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    declared_on = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intermediary_conflict_of_interest", x => x.conflict_id);
                    table.ForeignKey(
                        name: "fk_intermediary_coi_intermediary",
                        column: x => x.intermediary_id,
                        principalSchema: "pty",
                        principalTable: "intermediary",
                        principalColumn: "intermediary_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "intermediary_cpd",
                schema: "pty",
                columns: table => new
                {
                    intermediary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    hours = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    required_hours = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intermediary_cpd", x => new { x.intermediary_id, x.year });
                    table.ForeignKey(
                        name: "fk_intermediary_cpd_intermediary",
                        column: x => x.intermediary_id,
                        principalSchema: "pty",
                        principalTable: "intermediary",
                        principalColumn: "intermediary_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intermediary_register_number",
                schema: "pty",
                table: "intermediary",
                columns: new[] { "legal_entity_id", "register_number" });

            migrationBuilder.CreateIndex(
                name: "ix_intermediary_appointment_intermediary",
                schema: "pty",
                table: "intermediary_appointment",
                column: "intermediary_id");

            migrationBuilder.CreateIndex(
                name: "ix_intermediary_coi_intermediary",
                schema: "pty",
                table: "intermediary_conflict_of_interest",
                column: "intermediary_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "intermediary_appointment",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "intermediary_conflict_of_interest",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "intermediary_cpd",
                schema: "pty");

            migrationBuilder.DropIndex(
                name: "ix_intermediary_register_number",
                schema: "pty",
                table: "intermediary");

            migrationBuilder.DropColumn(
                name: "parent_intermediary_id",
                schema: "pty",
                table: "intermediary");
        }
    }
}
