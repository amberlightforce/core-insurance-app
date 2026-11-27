using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Channels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PortalEngagement : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_open",
                schema: "chn",
                columns: table => new
                {
                    document_open_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_opened_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    last_opened_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    open_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_open", x => x.document_open_id);
                    table.CheckConstraint("ck_document_open_count", "open_count >= 1");
                });

            migrationBuilder.CreateTable(
                name: "message",
                schema: "chn",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    topic = table.Column<string>(type: "text", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    sent_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message", x => x.message_id);
                    table.CheckConstraint("ck_message_direction", "direction IN ('CUSTOMER', 'STAFF')");
                    table.CheckConstraint("ck_message_topic", "topic IN ('GENERAL', 'POLICY', 'BILLING', 'CLAIM', 'DOCUMENT')");
                });

            migrationBuilder.CreateTable(
                name: "notification",
                schema: "chn",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    notification_class = table.Column<string>(type: "text", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: true),
                    subject_id = table.Column<string>(type: "text", nullable: true),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    read_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification", x => x.notification_id);
                    table.CheckConstraint("ck_notification_class", "notification_class IN ('RENEWAL', 'BILLING', 'CLAIM', 'DOCUMENT', 'MESSAGE')");
                    table.CheckConstraint("ck_notification_kind", "kind IN ('RENEWAL_OFFERED', 'INVOICE_ISSUED', 'CLAIM_UPDATED', 'DOCUMENT_READY', 'MESSAGE_REPLY')");
                });

            migrationBuilder.CreateTable(
                name: "notification_subscription",
                schema: "chn",
                columns: table => new
                {
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_class = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_subscription", x => x.subscription_id);
                    table.CheckConstraint("ck_notification_subscription_class", "notification_class IN ('RENEWAL', 'BILLING', 'CLAIM', 'DOCUMENT', 'MESSAGE')");
                });

            migrationBuilder.CreateIndex(
                name: "ux_document_open_party_document",
                schema: "chn",
                table: "document_open",
                columns: new[] { "legal_entity_id", "party_id", "document_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_message_party",
                schema: "chn",
                table: "message",
                columns: new[] { "legal_entity_id", "party_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_message_thread",
                schema: "chn",
                table: "message",
                columns: new[] { "legal_entity_id", "thread_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_party",
                schema: "chn",
                table: "notification",
                columns: new[] { "legal_entity_id", "party_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_notification_source",
                schema: "chn",
                table: "notification",
                columns: new[] { "legal_entity_id", "party_id", "kind", "source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_notification_subscription_class",
                schema: "chn",
                table: "notification_subscription",
                columns: new[] { "legal_entity_id", "party_id", "notification_class" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_open",
                schema: "chn");

            migrationBuilder.DropTable(
                name: "message",
                schema: "chn");

            migrationBuilder.DropTable(
                name: "notification",
                schema: "chn");

            migrationBuilder.DropTable(
                name: "notification_subscription",
                schema: "chn");
        }
    }
}
