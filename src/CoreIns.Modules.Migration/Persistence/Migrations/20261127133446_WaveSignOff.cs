using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Migration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WaveSignOff : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signoff",
                schema: "mig",
                columns: table => new
                {
                    signoff_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    results_hash = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signoff", x => x.signoff_id);
                });

            migrationBuilder.CreateTable(
                name: "signoff_waiver",
                schema: "mig",
                columns: table => new
                {
                    waiver_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variance_key = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    waived_by = table.Column<string>(type: "text", nullable: false),
                    waived_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signoff_waiver", x => x.waiver_id);
                });

            migrationBuilder.CreateTable(
                name: "signoff_signatory",
                schema: "mig",
                columns: table => new
                {
                    signatory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signoff_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signatory = table.Column<string>(type: "text", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signoff_signatory", x => x.signatory_id);
                    table.CheckConstraint("ck_signoff_signatory_status", "status IN ('PENDING','APPROVED','REJECTED')");
                    table.ForeignKey(
                        name: "fk_signoff_signatory_signoff",
                        column: x => x.signoff_id,
                        principalSchema: "mig",
                        principalTable: "signoff",
                        principalColumn: "signoff_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_signoff_scope",
                schema: "mig",
                table: "signoff",
                columns: new[] { "legal_entity_id", "scope", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_signoff_signatory",
                schema: "mig",
                table: "signoff_signatory",
                columns: new[] { "signoff_id", "signatory" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_signoff_waiver_key",
                schema: "mig",
                table: "signoff_waiver",
                columns: new[] { "legal_entity_id", "run_id", "variance_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signoff_signatory",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "signoff_waiver",
                schema: "mig");

            migrationBuilder.DropTable(
                name: "signoff",
                schema: "mig");
        }
    }
}
