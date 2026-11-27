using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProducerOfRecord : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "producer_of_record",
                schema: "pty",
                columns: table => new
                {
                    por_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intermediary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    producer_code = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    commission_consequence = table.Column<string>(type: "text", nullable: false),
                    split_share = table.Column<decimal>(type: "numeric(7,4)", nullable: true),
                    letter_reference = table.Column<string>(type: "text", nullable: true),
                    transfer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_producer_of_record", x => x.por_id);
                    table.CheckConstraint("ck_producer_of_record_consequence", "commission_consequence IN ('NEW_PRODUCER_FROM_DATE','OLD_PRODUCER_KEEPS_EARNED','OLD_PRODUCER_KEEPS_ALL')");
                    table.CheckConstraint("ck_producer_of_record_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_producer_of_record_split", "split_share IS NULL OR (split_share > 0 AND split_share <= 100)");
                    table.CheckConstraint("ck_producer_of_record_valid", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_producer_of_record_intermediary",
                        column: x => x.intermediary_id,
                        principalSchema: "pty",
                        principalTable: "intermediary",
                        principalColumn: "intermediary_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_producer_of_record_intermediary",
                schema: "pty",
                table: "producer_of_record",
                columns: new[] { "intermediary_id", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ux_producer_of_record_current_start",
                schema: "pty",
                table: "producer_of_record",
                columns: new[] { "policy_id", "valid_from" },
                unique: true,
                filter: "recorded_to IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "producer_of_record",
                schema: "pty");
        }
    }
}
