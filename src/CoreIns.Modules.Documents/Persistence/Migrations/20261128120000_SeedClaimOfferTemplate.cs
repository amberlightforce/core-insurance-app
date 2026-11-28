using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Documents.Persistence.Migrations
{
    /// <summary>
    /// Seeds the statutory offer letter of a third-party MTPL exposure (BO-CLM-06): data only, the model is unchanged. The text is an illustrative draft,
    /// not approved legal wording.
    /// </summary>
    [DbContext(typeof(DocumentsDbContext))]
    [Migration("20261128120000_SeedClaimOfferTemplate")]
    public partial class SeedClaimOfferTemplate : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO doc.document_type (document_type, default_medium, title_el, title_en, required_fields, customer_visible)
                VALUES ('DT-CLM-OFFER', 'EMAIL', $q$Αιτιολογημένη προσφορά ή απάντηση$q$, $q$Reasoned offer or reply$q$, ARRAY['claimNumber','exposureNumber','kind','amount','issuedOn','content'], true);
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.template (template_id, document_type, version, status, valid_from, title_el, body_el, title_en, body_en, approved_at, approved_by)
                VALUES (gen_random_uuid(), 'DT-CLM-OFFER', 1, 'APPROVED', DATE '2000-01-01', $q$Αιτιολογημένη προσφορά ή απάντηση$q$, $q$<p>Ζημία {{claimNumber}} / έκθεση {{exposureNumber}}, {{issuedOn}}. Είδος: {{kind}}. Ποσό: {{amount}}.</p><p>{{content}}</p>$q$, $q$Reasoned offer or reply$q$, $q$<p>Claim {{claimNumber}} / exposure {{exposureNumber}}, {{issuedOn}}. Type: {{kind}}. Amount: {{amount}}.</p><p>{{content}}</p>$q$, now(), 'seed');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM doc.template WHERE document_type = 'DT-CLM-OFFER';");
            migrationBuilder.Sql("DELETE FROM doc.document_type WHERE document_type = 'DT-CLM-OFFER';");
        }
    }
}
