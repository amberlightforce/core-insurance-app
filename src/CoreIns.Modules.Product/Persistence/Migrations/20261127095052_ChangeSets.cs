using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Product.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChangeSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_set",
                schema: "pfc",
                columns: table => new
                {
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    draft_json = table.Column<string>(type: "jsonb", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_set", x => x.change_set_id);
                    table.CheckConstraint("ck_change_set_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_change_set_status", "status IN ('OPEN', 'ABANDONED', 'SUBMITTED')");
                    table.ForeignKey(
                        name: "fk_change_set_base_version",
                        column: x => x.base_version_id,
                        principalSchema: "pfc",
                        principalTable: "product_version",
                        principalColumn: "product_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_set_product",
                        column: x => x.product_id,
                        principalSchema: "pfc",
                        principalTable: "product",
                        principalColumn: "product_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "change_set_edit",
                schema: "pfc",
                columns: table => new
                {
                    edit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    op = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    old_json = table.Column<string>(type: "jsonb", nullable: true),
                    new_json = table.Column<string>(type: "jsonb", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_set_edit", x => x.edit_id);
                    table.CheckConstraint("ck_change_set_edit_op", "op IN ('add', 'replace', 'remove')");
                    table.ForeignKey(
                        name: "fk_change_set_edit_change_set",
                        column: x => x.change_set_id,
                        principalSchema: "pfc",
                        principalTable: "change_set",
                        principalColumn: "change_set_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_change_set_base_version_id",
                schema: "pfc",
                table: "change_set",
                column: "base_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_set_product",
                schema: "pfc",
                table: "change_set",
                columns: new[] { "legal_entity_id", "product_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_change_set_product_id",
                schema: "pfc",
                table: "change_set",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_change_set_edit_seq",
                schema: "pfc",
                table: "change_set_edit",
                columns: new[] { "change_set_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_set_edit",
                schema: "pfc");

            migrationBuilder.DropTable(
                name: "change_set",
                schema: "pfc");
        }
    }
}
