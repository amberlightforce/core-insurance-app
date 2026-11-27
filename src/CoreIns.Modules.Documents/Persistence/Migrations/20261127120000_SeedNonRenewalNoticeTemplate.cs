using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Documents.Persistence.Migrations
{
    /// <summary>
    /// Seeds the non-renewal notice letter (BO-POL-09): data only, the model is unchanged. The text is an illustrative draft, not approved legal wording.
    /// </summary>
    [DbContext(typeof(DocumentsDbContext))]
    [Migration("20261127120000_SeedNonRenewalNoticeTemplate")]
    public partial class SeedNonRenewalNoticeTemplate : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO doc.document_type (document_type, default_medium, title_el, title_en, required_fields)
                VALUES ('DT-NONRENEWAL-NOTICE', 'EMAIL', $q$Ειδοποίηση μη ανανέωσης ασφαλιστηρίου$q$, $q$Notice of policy non-renewal$q$, ARRAY['customerName','policyNo','expiryDate']);
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.template (template_id, document_type, version, status, valid_from, title_el, body_el, title_en, body_en, approved_at, approved_by)
                VALUES (gen_random_uuid(), 'DT-NONRENEWAL-NOTICE', 1, 'APPROVED', DATE '2000-01-01', $q$Ειδοποίηση μη ανανέωσης ασφαλιστηρίου$q$, $q$<p>Αγαπητέ/ή {{customerName}},</p><p>Το ασφαλιστήριο {{policyNo}} δεν θα ανανεωθεί και λήγει στις {{expiryDate}}.</p>$q$, $q$Notice of policy non-renewal$q$, $q$<p>Dear {{customerName}},</p><p>Policy {{policyNo}} will not be renewed and expires on {{expiryDate}}.</p>$q$, now(), 'seed');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM doc.template WHERE document_type = 'DT-NONRENEWAL-NOTICE';");
            migrationBuilder.Sql("DELETE FROM doc.document_type WHERE document_type = 'DT-NONRENEWAL-NOTICE';");
        }
    }
}
