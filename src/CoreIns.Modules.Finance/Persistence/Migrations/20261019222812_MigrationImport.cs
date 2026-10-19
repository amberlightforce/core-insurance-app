using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrationImport : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_import",
                schema: "fin",
                columns: table => new
                {
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legacy_id = table.Column<string>(type: "text", nullable: false),
                    book = table.Column<string>(type: "text", nullable: false),
                    accounting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    trial_balance_debit = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    trial_balance_credit = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    imported_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_import", x => x.journal_id);
                    table.CheckConstraint("ck_migration_import_reconciled", "trial_balance_debit = trial_balance_credit");
                    table.ForeignKey(
                        name: "fk_migration_import_journal",
                        column: x => x.journal_id,
                        principalSchema: "fin",
                        principalTable: "journal_entry",
                        principalColumn: "journal_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_import_batch",
                schema: "fin",
                table: "migration_import",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ux_migration_import_legacy",
                schema: "fin",
                table: "migration_import",
                columns: new[] { "legal_entity_id", "legacy_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "migration_import",
                schema: "fin");
        }
    }
}
