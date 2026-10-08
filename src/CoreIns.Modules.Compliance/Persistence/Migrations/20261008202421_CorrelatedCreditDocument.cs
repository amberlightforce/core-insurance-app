using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Compliance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CorrelatedCreditDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "correlated_document_id",
                schema: "cmp",
                table: "fiscal_document",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "correlated_document_id",
                schema: "cmp",
                table: "fiscal_document");
        }
    }
}
