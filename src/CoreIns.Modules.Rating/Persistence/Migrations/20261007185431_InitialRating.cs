using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreIns.Modules.Rating.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rat");

            migrationBuilder.CreateTable(
                name: "rate_table_version",
                schema: "rat",
                columns: table => new
                {
                    table_hash = table.Column<string>(type: "char(64)", nullable: false),
                    table_code = table.Column<string>(type: "text", nullable: false),
                    version_no = table.Column<string>(type: "text", nullable: false),
                    hit_policy = table.Column<string>(type: "text", nullable: false),
                    data_status = table.Column<string>(type: "text", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_table_version", x => x.table_hash);
                    table.CheckConstraint("ck_rate_table_version_hash", "table_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_rate_table_version_status", "data_status IN ('ILLUSTRATIVE_TEST_DATA', 'APPROVED')");
                });

            migrationBuilder.CreateTable(
                name: "rating_artifact",
                schema: "rat",
                columns: table => new
                {
                    artefact_hash = table.Column<string>(type: "char(64)", nullable: false),
                    artefact_code = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: false),
                    engine_version = table.Column<string>(type: "text", nullable: false),
                    data_status = table.Column<string>(type: "text", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rating_artifact", x => x.artefact_hash);
                    table.CheckConstraint("ck_rating_artifact_hash", "artefact_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_rating_artifact_status", "data_status IN ('ILLUSTRATIVE_TEST_DATA', 'APPROVED')");
                });

            migrationBuilder.CreateTable(
                name: "rate_activation",
                schema: "rat",
                columns: table => new
                {
                    activation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artefact_hash = table.Column<string>(type: "char(64)", nullable: false),
                    product_code = table.Column<string>(type: "text", nullable: false),
                    product_version = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_activation", x => x.activation_id);
                    table.CheckConstraint("ck_rate_activation_range", "effective_to IS NULL OR effective_to > effective_from");
                    table.CheckConstraint("ck_rate_activation_status", "status IN ('Scheduled', 'Active', 'Superseded', 'Cancelled', 'RolledBack')");
                    table.ForeignKey(
                        name: "fk_rate_activation_artifact",
                        column: x => x.artefact_hash,
                        principalSchema: "rat",
                        principalTable: "rating_artifact",
                        principalColumn: "artefact_hash",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "worksheet",
                schema: "rat",
                columns: table => new
                {
                    worksheet_id = table.Column<string>(type: "char(64)", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    artefact_hash = table.Column<string>(type: "char(64)", nullable: false),
                    configuration_hash = table.Column<string>(type: "char(64)", nullable: false),
                    input_hash = table.Column<string>(type: "char(64)", nullable: false),
                    engine_version = table.Column<string>(type: "text", nullable: false),
                    data_status = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worksheet", x => x.worksheet_id);
                    table.CheckConstraint("ck_worksheet_id", "worksheet_id ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_worksheet_status", "data_status IN ('ILLUSTRATIVE_TEST_DATA', 'APPROVED')");
                    table.ForeignKey(
                        name: "fk_worksheet_artifact",
                        column: x => x.artefact_hash,
                        principalSchema: "rat",
                        principalTable: "rating_artifact",
                        principalColumn: "artefact_hash",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "worksheet_index",
                schema: "rat",
                columns: table => new
                {
                    index_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    worksheet_id = table.Column<string>(type: "char(64)", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "text", nullable: false),
                    retention_state = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worksheet_index", x => x.index_id);
                    table.CheckConstraint("ck_worksheet_index_retention", "retention_state IN ('QUOTE', 'ATTACHED', 'DRY_RUN')");
                    table.ForeignKey(
                        name: "fk_worksheet_index_worksheet",
                        column: x => x.worksheet_id,
                        principalSchema: "rat",
                        principalTable: "worksheet",
                        principalColumn: "worksheet_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rate_activation_artefact_hash",
                schema: "rat",
                table: "rate_activation",
                column: "artefact_hash");

            migrationBuilder.CreateIndex(
                name: "ux_rate_activation_product_from",
                schema: "rat",
                table: "rate_activation",
                columns: new[] { "product_code", "product_version", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rate_table_version_code",
                schema: "rat",
                table: "rate_table_version",
                columns: new[] { "table_code", "version_no" });

            migrationBuilder.CreateIndex(
                name: "ix_rating_artifact_product",
                schema: "rat",
                table: "rating_artifact",
                columns: new[] { "product_code", "product_version" });

            migrationBuilder.CreateIndex(
                name: "ix_worksheet_artifact",
                schema: "rat",
                table: "worksheet",
                column: "artefact_hash");

            migrationBuilder.CreateIndex(
                name: "ix_worksheet_index_quote",
                schema: "rat",
                table: "worksheet_index",
                column: "quote_id");

            migrationBuilder.CreateIndex(
                name: "ix_worksheet_index_worksheet",
                schema: "rat",
                table: "worksheet_index",
                column: "worksheet_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_activation",
                schema: "rat");

            migrationBuilder.DropTable(
                name: "rate_table_version",
                schema: "rat");

            migrationBuilder.DropTable(
                name: "worksheet_index",
                schema: "rat");

            migrationBuilder.DropTable(
                name: "worksheet",
                schema: "rat");

            migrationBuilder.DropTable(
                name: "rating_artifact",
                schema: "rat");
        }
    }
}
