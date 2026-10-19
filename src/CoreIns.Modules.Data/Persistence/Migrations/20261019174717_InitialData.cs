using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dat");

            migrationBuilder.CreateTable(
                name: "bias_report",
                schema: "dat",
                columns: table => new
                {
                    bias_report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: true),
                    feature_key = table.Column<string>(type: "text", nullable: false),
                    metric = table.Column<string>(type: "text", nullable: false),
                    proxy_group = table.Column<string>(type: "text", nullable: false),
                    ratio = table.Column<decimal>(type: "numeric(12,6)", nullable: false),
                    sample_size = table.Column<int>(type: "integer", nullable: false),
                    breached = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bias_report", x => x.bias_report_id);
                });

            migrationBuilder.CreateTable(
                name: "erasure_request",
                schema: "dat",
                columns: table => new
                {
                    request_id = table.Column<string>(type: "text", nullable: false),
                    subject_token = table.Column<string>(type: "text", nullable: false),
                    dsar_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    executed_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erasure_request", x => x.request_id);
                });

            migrationBuilder.CreateTable(
                name: "model",
                schema: "dat",
                columns: table => new
                {
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_key = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    card = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    suspended_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model", x => x.model_id);
                    table.CheckConstraint("ck_model_status", "status IN ('ACTIVE','SUSPENDED')");
                });

            migrationBuilder.CreateTable(
                name: "subject_fact",
                schema: "dat",
                columns: table => new
                {
                    fact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_token = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    fact_key = table.Column<string>(type: "text", nullable: false),
                    cipher = table.Column<byte[]>(type: "bytea", nullable: true),
                    blanked = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subject_fact", x => x.fact_id);
                });

            migrationBuilder.CreateTable(
                name: "subject_key",
                schema: "dat",
                columns: table => new
                {
                    subject_token = table.Column<string>(type: "text", nullable: false),
                    key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_material = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    destroyed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    restricted_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subject_key", x => x.subject_token);
                    table.CheckConstraint("ck_subject_key_destroyed", "destroyed_at IS NULL OR key_material IS NULL");
                });

            migrationBuilder.CreateIndex(
                name: "ux_model_feature_version",
                schema: "dat",
                table: "model",
                columns: new[] { "feature_key", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subject_fact_subject",
                schema: "dat",
                table: "subject_fact",
                column: "subject_token");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bias_report",
                schema: "dat");

            migrationBuilder.DropTable(
                name: "erasure_request",
                schema: "dat");

            migrationBuilder.DropTable(
                name: "model",
                schema: "dat");

            migrationBuilder.DropTable(
                name: "subject_fact",
                schema: "dat");

            migrationBuilder.DropTable(
                name: "subject_key",
                schema: "dat");
        }
    }
}
