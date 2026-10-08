using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approval_request",
                schema: "plt",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity = table.Column<string>(type: "text", nullable: false),
                    approval_type = table.Column<string>(type: "text", nullable: false),
                    object_module = table.Column<string>(type: "text", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    object_id = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    maker_kind = table.Column<string>(type: "text", nullable: false),
                    maker_id = table.Column<string>(type: "text", nullable: false),
                    maker_on_behalf_of_kind = table.Column<string>(type: "text", nullable: true),
                    maker_on_behalf_of_id = table.Column<string>(type: "text", nullable: true),
                    editors = table.Column<string[]>(type: "text[]", nullable: false),
                    authority_type = table.Column<string>(type: "text", nullable: false),
                    authority_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    authority_currency = table.Column<string>(type: "text", nullable: true),
                    authority_codes = table.Column<string>(type: "jsonb", nullable: false),
                    referral_role = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    diff = table.Column<string>(type: "jsonb", nullable: true),
                    supersedes = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: true),
                    checker_kind = table.Column<string>(type: "text", nullable: true),
                    checker_id = table.Column<string>(type: "text", nullable: true),
                    decision_comment = table.Column<string>(type: "text", nullable: true),
                    authority_check_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_request", x => x.request_id);
                    table.CheckConstraint("ck_approval_request_amount", "(authority_amount IS NULL) = (authority_currency IS NULL)");
                    table.CheckConstraint("ck_approval_request_checker_kind", "checker_kind IS NULL OR checker_kind = 'USER'");
                    table.CheckConstraint("ck_approval_request_codes", "jsonb_typeof(authority_codes) = 'object'");
                    table.CheckConstraint("ck_approval_request_currency", "authority_currency IS NULL OR authority_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_approval_request_decision", "(status IN ('Approved', 'Rejected') AND decision = status AND checker_kind IS NOT NULL AND checker_id IS NOT NULL AND authority_check_id IS NOT NULL AND decided_at IS NOT NULL) OR (status = 'PendingApproval' AND decision IS NULL AND checker_id IS NULL AND decided_at IS NULL) OR (status = 'Withdrawn' AND decision IS NULL AND checker_id IS NULL AND decided_at IS NOT NULL)");
                    table.CheckConstraint("ck_approval_request_diff", "diff IS NULL OR jsonb_typeof(diff) = 'object'");
                    table.CheckConstraint("ck_approval_request_hash", "payload_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_approval_request_maker_kind", "maker_kind IN ('USER', 'SERVICE', 'AI_AGENT')");
                    table.CheckConstraint("ck_approval_request_maker_principal", "(maker_on_behalf_of_kind IS NULL) = (maker_on_behalf_of_id IS NULL) AND (maker_on_behalf_of_kind IS NULL OR maker_on_behalf_of_kind IN ('USER', 'SERVICE', 'AI_AGENT'))");
                    table.CheckConstraint("ck_approval_request_reject_comment", "decision IS DISTINCT FROM 'Rejected' OR decision_comment IS NOT NULL");
                    table.CheckConstraint("ck_approval_request_status", "status IN ('PendingApproval', 'Approved', 'Rejected', 'Withdrawn')");
                    table.CheckConstraint("ck_approval_request_version", "version >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_request_inbox",
                schema: "plt",
                table: "approval_request",
                columns: new[] { "legal_entity", "status", "referral_role", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_approval_request_subject",
                schema: "plt",
                table: "approval_request",
                columns: new[] { "object_module", "object_type", "object_id" });

            migrationBuilder.CreateIndex(
                name: "ux_approval_request_pending_subject",
                schema: "plt",
                table: "approval_request",
                columns: new[] { "legal_entity", "approval_type", "object_module", "object_type", "object_id" },
                unique: true,
                filter: "status = 'PendingApproval'");

            // Hand-written (SL2-PLT; review m1): a request is decided once. Only a pending row may change, only into a
            // decided or withdrawn state, and only the decision columns may change with it; every other column (subject,
            // content hash, maker and principal, editors, authority, referral role, reason, diff, supersedes, …) is frozen.
            // Rows are never deleted.
            migrationBuilder.Sql("""
                CREATE FUNCTION plt.approval_request_guard() RETURNS trigger
                  LANGUAGE plpgsql SET search_path = pg_catalog, plt AS $$
                DECLARE
                  decision_columns text[] := ARRAY['status', 'decision', 'checker_kind', 'checker_id', 'decision_comment',
                                                   'authority_check_id', 'decided_at', 'version'];
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'SECURITY: plt.approval_request rows are never deleted' USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  IF OLD.status <> 'PendingApproval' OR NEW.status = 'PendingApproval' THEN
                    RAISE EXCEPTION 'SECURITY: approval request % is % and cannot change to %', OLD.request_id, OLD.status, NEW.status
                      USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  IF (to_jsonb(NEW) - decision_columns) IS DISTINCT FROM (to_jsonb(OLD) - decision_columns) OR NEW.version <> OLD.version + 1 THEN
                    RAISE EXCEPTION 'SECURITY: approval request % content is immutable', OLD.request_id USING ERRCODE = 'insufficient_privilege';
                  END IF;
                  RETURN NEW;
                END
                $$;
                CREATE TRIGGER trg_approval_request_guard BEFORE UPDATE OR DELETE ON plt.approval_request
                  FOR EACH ROW EXECUTE FUNCTION plt.approval_request_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_approval_request_guard ON plt.approval_request; DROP FUNCTION IF EXISTS plt.approval_request_guard();");

            migrationBuilder.DropTable(
                name: "approval_request",
                schema: "plt");
        }
    }
}
