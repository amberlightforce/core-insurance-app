using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Finance.Persistence.Migrations
{
    /// <summary>
    /// SL3-E2E integration fix: FIN consumes <c>pol.RenewalBound</c> as policy CONTEXT (like PolicyBound) so that the BIL entries of
    /// a renewal term (written, billed, IPT due) are released and posted. Data only (one catalogue row); no model change.
    /// </summary>
    [DbContext(typeof(FinanceDbContext))]
    [Migration("20261009120000_RenewalContext")]
    public partial class RenewalContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO fin.event_catalogue (registry_name, schema_major, relevance, amount_fields, note)
                VALUES ('pol.RenewalBound', 1, 'CONTEXT', '', 'Policy context of renewal term n+1 (term id, product and artefact hash for GL-key derivation); never journalised (REQ-FIN-036, D-SL3-10)')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Catalogue rows are reference data; nothing is deleted.
        }
    }
}
