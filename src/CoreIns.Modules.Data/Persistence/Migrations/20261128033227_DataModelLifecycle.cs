using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataModelLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_model_status",
                schema: "dat",
                table: "model");

            migrationBuilder.AddColumn<string>(
                name: "approved_by",
                schema: "dat",
                table: "model",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "created_by",
                schema: "dat",
                table: "model",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "drift_flagged",
                schema: "dat",
                table: "model",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "drift_reason",
                schema: "dat",
                table: "model",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "effective_date",
                schema: "dat",
                table: "model",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "retired_reason",
                schema: "dat",
                table: "model",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "submitted_by",
                schema: "dat",
                table: "model",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validated_by",
                schema: "dat",
                table: "model",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validation_findings",
                schema: "dat",
                table: "model",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validation_outcome",
                schema: "dat",
                table: "model",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "model_metric",
                schema: "dat",
                columns: table => new
                {
                    metric_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    threshold = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    breached = table.Column<bool>(type: "boolean", nullable: false),
                    sample_size = table.Column<int>(type: "integer", nullable: false),
                    window_from = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    window_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    breach_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_metric", x => x.metric_id);
                });

            migrationBuilder.CreateTable(
                name: "model_transition",
                schema: "dat",
                columns: table => new
                {
                    transition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    actor_key = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_transition", x => x.transition_id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_status",
                schema: "dat",
                table: "model",
                sql: "status IN ('DRAFT','IN_VALIDATION','APPROVED','ACTIVE','SUSPENDED','RETIRED')");

            migrationBuilder.CreateIndex(
                name: "ix_model_metric_model_time",
                schema: "dat",
                table: "model_metric",
                columns: new[] { "model_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_model_transition_model",
                schema: "dat",
                table: "model_transition",
                column: "model_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_metric",
                schema: "dat");

            migrationBuilder.DropTable(
                name: "model_transition",
                schema: "dat");

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_status",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "approved_by",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "created_by",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "drift_flagged",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "drift_reason",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "effective_date",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "retired_reason",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "submitted_by",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "validated_by",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "validation_findings",
                schema: "dat",
                table: "model");

            migrationBuilder.DropColumn(
                name: "validation_outcome",
                schema: "dat",
                table: "model");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_status",
                schema: "dat",
                table: "model",
                sql: "status IN ('ACTIVE','SUSPENDED')");
        }
    }
}
