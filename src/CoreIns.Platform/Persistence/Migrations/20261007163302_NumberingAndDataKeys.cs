using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NumberingAndDataKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_key",
                schema: "plt",
                columns: table => new
                {
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    kek_id = table.Column<string>(type: "text", nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    demoted_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    retiring_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_key", x => new { x.legal_entity_id, x.purpose, x.version });
                    table.CheckConstraint("ck_data_key_purpose", "purpose IN (1, 2)");
                    table.CheckConstraint("ck_data_key_status", "status IN (1, 2, 3, 4)");
                    table.CheckConstraint("ck_data_key_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "number_series",
                schema: "plt",
                columns: table => new
                {
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    identifier_type = table.Column<string>(type: "text", nullable: false),
                    series_id = table.Column<string>(type: "text", nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false),
                    max_value = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_number_series", x => new { x.legal_entity, x.identifier_type, x.series_id });
                    table.CheckConstraint("ck_number_series_range", "next_value >= 1 AND max_value >= 1 AND next_value <= max_value + 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_key",
                schema: "plt");

            migrationBuilder.DropTable(
                name: "number_series",
                schema: "plt");
        }
    }
}
