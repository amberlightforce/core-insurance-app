using System;
using Microsoft.EntityFrameworkCore.Migrations;
using CoreIns.Modules.Reinsurance.Persistence;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Programmes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "programme",
                schema: "ri",
                columns: table => new
                {
                    programme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    line_code = table.Column<string>(type: "text", nullable: false),
                    programme_year = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    renewed_from_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_version_no = table.Column<int>(type: "integer", nullable: false),
                    participants = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_programme", x => x.programme_id);
                    table.CheckConstraint("ck_programme_currency", "currency = 'EUR'");
                    table.CheckConstraint("ck_programme_jurisdiction", "jurisdiction ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_programme_participants", "cardinality(participants) >= 1");
                    table.CheckConstraint("ck_programme_record_version", "record_version >= 1 AND current_version_no >= 1");
                    table.CheckConstraint("ck_programme_year", "programme_year BETWEEN 1990 AND 2200");
                    table.ForeignKey(
                        name: "fk_programme_renewed_from",
                        column: x => x.renewed_from_id,
                        principalSchema: "ri",
                        principalTable: "programme",
                        principalColumn: "programme_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "programme_version",
                schema: "ri",
                columns: table => new
                {
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    programme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_programme_version", x => x.version_id);
                    table.CheckConstraint("ck_programme_version_no", "version_no >= 1");
                    table.CheckConstraint("ck_programme_version_period", "valid_to > valid_from");
                    table.CheckConstraint("ck_programme_version_status", "status IN ('DRAFT', 'PENDING_APPROVAL', 'ACTIVE', 'SUPERSEDED', 'EXPIRED')");
                    table.ForeignKey(
                        name: "fk_programme_version_programme",
                        column: x => x.programme_id,
                        principalSchema: "ri",
                        principalTable: "programme",
                        principalColumn: "programme_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "programme_layer",
                schema: "ri",
                columns: table => new
                {
                    layer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer_no = table.Column<int>(type: "integer", nullable: false),
                    attachment = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    limit_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_programme_layer", x => x.layer_id);
                    table.CheckConstraint("ck_programme_layer_amounts", "attachment >= 0 AND limit_amount > 0");
                    table.CheckConstraint("ck_programme_layer_no", "layer_no >= 1");
                    table.ForeignKey(
                        name: "fk_programme_layer_contract",
                        column: x => x.contract_id,
                        principalSchema: "ri",
                        principalTable: "contract",
                        principalColumn: "contract_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_programme_layer_version",
                        column: x => x.version_id,
                        principalSchema: "ri",
                        principalTable: "programme_version",
                        principalColumn: "version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_programme_line",
                schema: "ri",
                table: "programme",
                columns: new[] { "legal_entity_id", "line_code" });

            migrationBuilder.CreateIndex(
                name: "IX_programme_renewed_from_id",
                schema: "ri",
                table: "programme",
                column: "renewed_from_id");

            migrationBuilder.CreateIndex(
                name: "ix_programme_year",
                schema: "ri",
                table: "programme",
                columns: new[] { "legal_entity_id", "programme_year" });

            migrationBuilder.CreateIndex(
                name: "ux_programme_code_year",
                schema: "ri",
                table: "programme",
                columns: new[] { "legal_entity_id", "code", "programme_year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_programme_layer_contract",
                schema: "ri",
                table: "programme_layer",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "ux_programme_layer_no",
                schema: "ri",
                table: "programme_layer",
                columns: new[] { "version_id", "layer_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_programme_version_no",
                schema: "ri",
                table: "programme_version",
                columns: new[] { "programme_id", "version_no" },
                unique: true);

            migrationBuilder.Sql(ProgrammeDatabaseSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ProgrammeDatabaseSql.Down);

            migrationBuilder.DropTable(
                name: "programme_layer",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "programme_version",
                schema: "ri");

            migrationBuilder.DropTable(
                name: "programme",
                schema: "ri");
        }
    }
}
