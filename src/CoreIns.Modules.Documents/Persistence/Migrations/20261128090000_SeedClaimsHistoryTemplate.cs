using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Documents.Persistence.Migrations
{
    /// <summary>
    /// Seeds the claims-history certificate letter (BO-CLM-07): data only, the model is unchanged. The text is an illustrative draft,
    /// not approved legal wording. It carries no personal data beyond the addressee (no AFM, no free text).
    /// </summary>
    [DbContext(typeof(DocumentsDbContext))]
    [Migration("20261128090000_SeedClaimsHistoryTemplate")]
    public partial class SeedClaimsHistoryTemplate : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO doc.document_type (document_type, default_medium, title_el, title_en, required_fields, customer_visible)
                VALUES ('DT-CLAIMS-HISTORY', 'EMAIL', $q$Βεβαίωση ιστορικού αποζημιώσεων$q$, $q$Claims-history certificate$q$, ARRAY['certificateNumber','periodFrom','periodTo','issuedOn','claimCount','claimsSummary'], true);
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.template (template_id, document_type, version, status, valid_from, title_el, body_el, title_en, body_en, approved_at, approved_by)
                VALUES (gen_random_uuid(), 'DT-CLAIMS-HISTORY', 1, 'APPROVED', DATE '2000-01-01', $q$Βεβαίωση ιστορικού αποζημιώσεων$q$, $q$<p>Βεβαίωση αριθμός {{certificateNumber}}, εκδοθείσα στις {{issuedOn}}.</p><p>Για το διάστημα από {{periodFrom}} έως {{periodTo}} καταγράφονται {{claimCount}} ζημίες: {{claimsSummary}}.</p>$q$, $q$Claims-history certificate$q$, $q$<p>Certificate number {{certificateNumber}}, issued on {{issuedOn}}.</p><p>For the period from {{periodFrom}} to {{periodTo}}, {{claimCount}} claims are recorded: {{claimsSummary}}.</p>$q$, now(), 'seed');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM doc.template WHERE document_type = 'DT-CLAIMS-HISTORY';");
            migrationBuilder.Sql("DELETE FROM doc.document_type WHERE document_type = 'DT-CLAIMS-HISTORY';");
        }
    }
}
