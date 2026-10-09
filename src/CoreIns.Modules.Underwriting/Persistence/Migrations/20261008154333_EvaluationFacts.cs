using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Underwriting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EvaluationFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "facts",
                schema: "uw",
                table: "evaluation",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "facts",
                schema: "uw",
                table: "evaluation");
        }
    }
}
