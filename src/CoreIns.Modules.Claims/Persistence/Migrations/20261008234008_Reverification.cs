using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Claims.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Reverification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reverification",
                schema: "clm",
                columns: table => new
                {
                    reverification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cause_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cause_event_type = table.Column<string>(type: "text", nullable: false),
                    old_snapshot_ref = table.Column<string>(type: "text", nullable: false),
                    new_snapshot_ref = table.Column<string>(type: "text", nullable: false),
                    raised_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: true),
                    comment_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    coverage_in_question = table.Column<bool>(type: "boolean", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reverification", x => x.reverification_id);
                    table.CheckConstraint("ck_reverification_decided_shape", "(status = 'OPEN') = (decided_at IS NULL)");
                    table.CheckConstraint("ck_reverification_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_reverification_reason", "status = 'OPEN' OR reason_code IS NOT NULL");
                    table.CheckConstraint("ck_reverification_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_reverification_refs", "old_snapshot_ref <> new_snapshot_ref");
                    table.CheckConstraint("ck_reverification_status", "status IN ('OPEN', 'KEPT', 'ADOPTED')");
                    table.ForeignKey(
                        name: "fk_reverification_claim",
                        column: x => x.claim_id,
                        principalSchema: "clm",
                        principalTable: "claim",
                        principalColumn: "claim_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reverification_claim_status",
                schema: "clm",
                table: "reverification",
                columns: new[] { "claim_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_reverification_claim_cause",
                schema: "clm",
                table: "reverification",
                columns: new[] { "claim_id", "cause_event_id" },
                unique: true);

            migrationBuilder.Sql(ReverificationSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReverificationSql.Down);

            migrationBuilder.DropTable(
                name: "reverification",
                schema: "clm");
        }
    }
}
