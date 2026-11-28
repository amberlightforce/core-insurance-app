using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Consents : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "communication_preference",
                schema: "pty",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    preference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    medium = table.Column<string>(type: "text", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_communication_preference", x => x.row_id);
                    table.CheckConstraint("ck_communication_preference_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_communication_preference_valid", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_communication_preference_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consent",
                schema: "pty",
                columns: table => new
                {
                    consent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    wording_version = table.Column<string>(type: "text", nullable: true),
                    proof_ref = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent", x => x.consent_id);
                    table.CheckConstraint("ck_consent_kind", "kind IN ('GIVEN','WITHDRAWN','OBJECTION')");
                    table.CheckConstraint("ck_consent_state", "state IN ('GRANTED','WITHDRAWN','OBJECTED')");
                    table.ForeignKey(
                        name: "fk_consent_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_communication_preference_party",
                schema: "pty",
                table: "communication_preference",
                columns: new[] { "party_id", "purpose" });

            migrationBuilder.CreateIndex(
                name: "ix_consent_party",
                schema: "pty",
                table: "consent",
                columns: new[] { "party_id", "purpose", "channel", "recorded_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "communication_preference",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "consent",
                schema: "pty");
        }
    }
}
