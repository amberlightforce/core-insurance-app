using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TemplateCatalogue : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "template_version",
                schema: "doc",
                table: "document",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clause",
                schema: "doc",
                columns: table => new
                {
                    clause_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    title_el = table.Column<string>(type: "text", nullable: false),
                    text_el = table.Column<string>(type: "text", nullable: false),
                    title_en = table.Column<string>(type: "text", nullable: false),
                    text_en = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clause", x => x.clause_id);
                    table.CheckConstraint("ck_clause_status", "status IN ('DRAFT','APPROVED')");
                });

            migrationBuilder.CreateTable(
                name: "document_type",
                schema: "doc",
                columns: table => new
                {
                    document_type = table.Column<string>(type: "text", nullable: false),
                    default_medium = table.Column<string>(type: "text", nullable: false),
                    title_el = table.Column<string>(type: "text", nullable: false),
                    title_en = table.Column<string>(type: "text", nullable: false),
                    required_fields = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_type", x => x.document_type);
                });

            migrationBuilder.CreateTable(
                name: "template",
                schema: "doc",
                columns: table => new
                {
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    product_code = table.Column<string>(type: "text", nullable: true),
                    line_of_business = table.Column<string>(type: "text", nullable: true),
                    jurisdiction = table.Column<string>(type: "text", nullable: true),
                    title_el = table.Column<string>(type: "text", nullable: false),
                    body_el = table.Column<string>(type: "text", nullable: false),
                    title_en = table.Column<string>(type: "text", nullable: false),
                    body_en = table.Column<string>(type: "text", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template", x => x.template_id);
                    table.CheckConstraint("ck_template_status", "status IN ('DRAFT','APPROVED')");
                    table.CheckConstraint("ck_template_valid", "valid_to IS NULL OR valid_to >= valid_from");
                });

            migrationBuilder.CreateIndex(
                name: "ux_clause_code",
                schema: "doc",
                table: "clause",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_template_version",
                schema: "doc",
                table: "template",
                columns: new[] { "document_type", "version" },
                unique: true);

            // Seed: the four SL6 templates (D-SL6-12), version 1, APPROVED. Texts are illustrative drafts.
            migrationBuilder.Sql("""
                INSERT INTO doc.document_type (document_type, default_medium, title_el, title_en, required_fields)
                VALUES ('DT-NONPAY-NOTICE', 'REGISTERED_POST', $q$Ειδοποίηση μη πληρωμής ασφαλίστρου$q$, $q$Notice of non-payment of premium$q$, ARRAY['customerName','policyNo','amountDue','dueDate']);
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.template (template_id, document_type, version, status, valid_from, title_el, body_el, title_en, body_en, approved_at, approved_by)
                VALUES (gen_random_uuid(), 'DT-NONPAY-NOTICE', 1, 'APPROVED', DATE '2000-01-01', $q$Ειδοποίηση μη πληρωμής ασφαλίστρου$q$, $q$<p>Αγαπητέ/ή {{customerName}},</p><p>Για το ασφαλιστήριο {{policyNo}} δεν έχει εξοφληθεί το ποσό {{amountDue}} που έληγε στις {{dueDate}}. Εάν δεν εξοφληθεί εντός της νόμιμης προθεσμίας από την κοινοποίηση της παρούσας, η ασφάλιση καταγγέλλεται.</p>$q$, $q$Notice of non-payment of premium$q$, $q$<p>Dear {{customerName}},</p><p>For policy {{policyNo}} the amount {{amountDue}} due on {{dueDate}} has not been paid. If it is not paid within the statutory period from notification of this letter, the cover will be cancelled.</p>$q$, now(), 'seed');
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.document_type (document_type, default_medium, title_el, title_en, required_fields)
                VALUES ('DT-WITHDRAWAL-ACK', 'EMAIL', $q$Επιβεβαίωση υπαναχώρησης$q$, $q$Withdrawal acknowledgement$q$, ARRAY['customerName','policyNo','withdrawalDate']);
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.template (template_id, document_type, version, status, valid_from, title_el, body_el, title_en, body_en, approved_at, approved_by)
                VALUES (gen_random_uuid(), 'DT-WITHDRAWAL-ACK', 1, 'APPROVED', DATE '2000-01-01', $q$Επιβεβαίωση υπαναχώρησης$q$, $q$<p>Αγαπητέ/ή {{customerName}},</p><p>Παραλάβαμε τη δήλωση υπαναχώρησής σας για το ασφαλιστήριο {{policyNo}} στις {{withdrawalDate}}.</p>$q$, $q$Withdrawal acknowledgement$q$, $q$<p>Dear {{customerName}},</p><p>We received your withdrawal from policy {{policyNo}} on {{withdrawalDate}}.</p>$q$, now(), 'seed');
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.document_type (document_type, default_medium, title_el, title_en, required_fields)
                VALUES ('DT-CANCELLATION-NOTICE', 'EMAIL', $q$Ειδοποίηση ακύρωσης ασφαλιστηρίου$q$, $q$Notice of policy cancellation$q$, ARRAY['customerName','policyNo','effectiveDate']);
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.template (template_id, document_type, version, status, valid_from, title_el, body_el, title_en, body_en, approved_at, approved_by)
                VALUES (gen_random_uuid(), 'DT-CANCELLATION-NOTICE', 1, 'APPROVED', DATE '2000-01-01', $q$Ειδοποίηση ακύρωσης ασφαλιστηρίου$q$, $q$<p>Αγαπητέ/ή {{customerName}},</p><p>Το ασφαλιστήριο {{policyNo}} ακυρώνεται με ισχύ από {{effectiveDate}}.</p>$q$, $q$Notice of policy cancellation$q$, $q$<p>Dear {{customerName}},</p><p>Policy {{policyNo}} is cancelled with effect from {{effectiveDate}}.</p>$q$, now(), 'seed');
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.document_type (document_type, default_medium, title_el, title_en, required_fields)
                VALUES ('DT-REFUND-ADVICE', 'EMAIL', $q$Ενημέρωση επιστροφής χρημάτων$q$, $q$Refund advice$q$, ARRAY['customerName','policyNo','refundAmount']);
                """);
            migrationBuilder.Sql("""
                INSERT INTO doc.template (template_id, document_type, version, status, valid_from, title_el, body_el, title_en, body_en, approved_at, approved_by)
                VALUES (gen_random_uuid(), 'DT-REFUND-ADVICE', 1, 'APPROVED', DATE '2000-01-01', $q$Ενημέρωση επιστροφής χρημάτων$q$, $q$<p>Αγαπητέ/ή {{customerName}},</p><p>Θα επιστραφεί το ποσό {{refundAmount}} για το ασφαλιστήριο {{policyNo}}.</p>$q$, $q$Refund advice$q$, $q$<p>Dear {{customerName}},</p><p>The amount {{refundAmount}} will be refunded for policy {{policyNo}}.</p>$q$, now(), 'seed');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clause",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "document_type",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "template",
                schema: "doc");

            migrationBuilder.DropColumn(
                name: "template_version",
                schema: "doc",
                table: "document");
        }
    }
}
