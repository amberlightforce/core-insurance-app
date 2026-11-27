using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Party.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartyRolesRelationships : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "party_relationship",
                schema: "pty",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_type = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_from = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party_relationship", x => x.row_id);
                    table.CheckConstraint("ck_party_relationship_distinct", "from_party_id <> to_party_id");
                    table.CheckConstraint("ck_party_relationship_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_party_relationship_valid", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_party_relationship_from",
                        column: x => x.from_party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_party_relationship_to",
                        column: x => x.to_party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "party_role",
                schema: "pty",
                columns: table => new
                {
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_type = table.Column<string>(type: "text", nullable: false),
                    context_module = table.Column<string>(type: "text", nullable: true),
                    context_type = table.Column<string>(type: "text", nullable: true),
                    context_id = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_party_role", x => x.row_id);
                    table.CheckConstraint("ck_party_role_recorded", "recorded_to IS NULL OR recorded_to > recorded_from");
                    table.CheckConstraint("ck_party_role_valid", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_party_role_party",
                        column: x => x.party_id,
                        principalSchema: "pty",
                        principalTable: "party",
                        principalColumn: "party_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_party_relationship_from",
                schema: "pty",
                table: "party_relationship",
                column: "from_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_party_relationship_rel",
                schema: "pty",
                table: "party_relationship",
                column: "relationship_id");

            migrationBuilder.CreateIndex(
                name: "ix_party_relationship_to",
                schema: "pty",
                table: "party_relationship",
                column: "to_party_id");

            migrationBuilder.CreateIndex(
                name: "ix_party_role_party",
                schema: "pty",
                table: "party_role",
                columns: new[] { "party_id", "role_type" });

            migrationBuilder.CreateIndex(
                name: "ix_party_role_role",
                schema: "pty",
                table: "party_role",
                column: "role_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_relationship",
                schema: "pty");

            migrationBuilder.DropTable(
                name: "party_role",
                schema: "pty");
        }
    }
}
