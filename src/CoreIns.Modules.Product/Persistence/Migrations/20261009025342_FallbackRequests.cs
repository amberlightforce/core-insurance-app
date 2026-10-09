using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Product.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FallbackRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "fallback_of_version_id",
                schema: "pfc",
                table: "product_version",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "replaces_version_id",
                schema: "pfc",
                table: "product_version",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fallback_request",
                schema: "pfc",
                columns: table => new
                {
                    fallback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    jurisdiction = table.Column<string>(type: "char(2)", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    defective_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    new_major = table.Column<int>(type: "integer", nullable: false),
                    new_minor = table.Column<int>(type: "integer", nullable: false),
                    fallback_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "char(64)", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    requested_by_principal = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    decision_reason = table.Column<string>(type: "text", nullable: true),
                    new_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    record_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fallback_request", x => x.fallback_id);
                    table.CheckConstraint("ck_fallback_applied", "(status = 'APPLIED') = (new_version_id IS NOT NULL)");
                    table.CheckConstraint("ck_fallback_decision", "(status = 'PENDING_APPROVAL') = (decided_at IS NULL AND decided_by IS NULL)");
                    table.CheckConstraint("ck_fallback_hash", "payload_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_fallback_new_number", "new_major >= 0 AND new_minor >= 0");
                    table.CheckConstraint("ck_fallback_reason", "char_length(reason) BETWEEN 20 AND 128");
                    table.CheckConstraint("ck_fallback_record_version", "record_version >= 1");
                    table.CheckConstraint("ck_fallback_sod", "decided_by IS NULL OR decided_by <> requested_by");
                    table.CheckConstraint("ck_fallback_status", "status IN ('PENDING_APPROVAL', 'APPLIED', 'REJECTED')");
                    table.ForeignKey(
                        name: "fk_fallback_defective",
                        column: x => x.defective_version_id,
                        principalSchema: "pfc",
                        principalTable: "product_version",
                        principalColumn: "product_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fallback_new_version",
                        column: x => x.new_version_id,
                        principalSchema: "pfc",
                        principalTable: "product_version",
                        principalColumn: "product_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fallback_product",
                        column: x => x.product_id,
                        principalSchema: "pfc",
                        principalTable: "product",
                        principalColumn: "product_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fallback_source",
                        column: x => x.source_version_id,
                        principalSchema: "pfc",
                        principalTable: "product_version",
                        principalColumn: "product_version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_version_fallback_of_version_id",
                schema: "pfc",
                table: "product_version",
                column: "fallback_of_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_version_replaces_version_id",
                schema: "pfc",
                table: "product_version",
                column: "replaces_version_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_version_fallback_pair",
                schema: "pfc",
                table: "product_version",
                sql: "(fallback_of_version_id IS NULL) = (replaces_version_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_fallback_request_new_version_id",
                schema: "pfc",
                table: "fallback_request",
                column: "new_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_fallback_request_product_id",
                schema: "pfc",
                table: "fallback_request",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_fallback_request_source_version_id",
                schema: "pfc",
                table: "fallback_request",
                column: "source_version_id");

            migrationBuilder.CreateIndex(
                name: "ux_fallback_approval",
                schema: "pfc",
                table: "fallback_request",
                column: "approval_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_fallback_live_per_defective",
                schema: "pfc",
                table: "fallback_request",
                column: "defective_version_id",
                unique: true,
                filter: "status <> 'REJECTED'");

            migrationBuilder.AddForeignKey(
                name: "fk_product_version_fallback_of",
                schema: "pfc",
                table: "product_version",
                column: "fallback_of_version_id",
                principalSchema: "pfc",
                principalTable: "product_version",
                principalColumn: "product_version_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_product_version_replaces",
                schema: "pfc",
                table: "product_version",
                column: "replaces_version_id",
                principalSchema: "pfc",
                principalTable: "product_version",
                principalColumn: "product_version_id",
                onDelete: ReferentialAction.Restrict);

            // PITFALLS 17/47: a Locked version is write-once. The app role has UPDATE on the table, so a trigger refuses every
            // change to a Locked row except setting or shortening the new-business end (and retiring).
            migrationBuilder.Sql(
                """
                CREATE FUNCTION pfc.guard_locked_version() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.fallback_of_version_id IS DISTINCT FROM OLD.fallback_of_version_id
                       OR NEW.replaces_version_id IS DISTINCT FROM OLD.replaces_version_id THEN
                        RAISE EXCEPTION 'pfc.product_version fall-back links are write-once (REQ-PFC-213)' USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    IF OLD.status IN ('LOCKED', 'RETIRED') THEN
                        IF NEW.status NOT IN ('LOCKED', 'RETIRED') OR (OLD.status = 'RETIRED' AND NEW.status <> 'RETIRED')
                           OR NEW.product_id <> OLD.product_id OR NEW.legal_entity_id <> OLD.legal_entity_id
                           OR NEW.jurisdiction <> OLD.jurisdiction OR NEW.major <> OLD.major OR NEW.minor <> OLD.minor
                           OR NEW.is_abstract <> OLD.is_abstract OR NEW.channels <> OLD.channels
                           OR NEW.contract_currency <> OLD.contract_currency OR NEW.artefact_hash <> OLD.artefact_hash
                           OR NEW.schema_version <> OLD.schema_version OR NEW.new_business_from <> OLD.new_business_from
                           OR NEW.renewal_from <> OLD.renewal_from OR NEW.renewal_to IS DISTINCT FROM OLD.renewal_to
                           OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by
                           OR NEW.locked_at IS DISTINCT FROM OLD.locked_at THEN
                            RAISE EXCEPTION 'a Locked pfc.product_version is write-once (REQ-PFC-197, REQ-PFC-213)' USING ERRCODE = 'integrity_constraint_violation';
                        END IF;
                        IF NEW.new_business_to IS DISTINCT FROM OLD.new_business_to
                           AND OLD.new_business_to IS NOT NULL AND (NEW.new_business_to IS NULL OR NEW.new_business_to >= OLD.new_business_to) THEN
                            RAISE EXCEPTION 'a new-business end can only be set or shortened, never extended (REQ-PFC-178)' USING ERRCODE = 'integrity_constraint_violation';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_locked_version_guard BEFORE UPDATE ON pfc.product_version
                    FOR EACH ROW EXECUTE FUNCTION pfc.guard_locked_version()
                """);

            // PITFALLS 47: fall-back requests move only PENDING_APPROVAL -> APPLIED | REJECTED, once, by someone other than the maker.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION pfc.guard_fallback_request() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status <> 'PENDING_APPROVAL' THEN
                        RAISE EXCEPTION 'a decided fall-back request is final (REQ-PFC-213)' USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    IF NEW.fallback_id <> OLD.fallback_id OR NEW.legal_entity_id <> OLD.legal_entity_id OR NEW.product_id <> OLD.product_id
                       OR NEW.defective_version_id <> OLD.defective_version_id OR NEW.source_version_id <> OLD.source_version_id
                       OR NEW.new_major <> OLD.new_major OR NEW.new_minor <> OLD.new_minor
                       OR NEW.reason <> OLD.reason OR NEW.payload_hash <> OLD.payload_hash
                       OR NEW.approval_request_id <> OLD.approval_request_id OR NEW.requested_by <> OLD.requested_by
                       OR NEW.requested_by_principal IS DISTINCT FROM OLD.requested_by_principal OR NEW.requested_at <> OLD.requested_at THEN
                        RAISE EXCEPTION 'the bound content of a fall-back request cannot change (REQ-PFC-213)' USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    IF NEW.status NOT IN ('APPLIED', 'REJECTED') OR NEW.decided_by IS NULL OR NEW.decided_at IS NULL
                       OR NEW.decided_by = OLD.requested_by OR NEW.decided_by IS NOT DISTINCT FROM OLD.requested_by_principal THEN
                        RAISE EXCEPTION 'a fall-back request is decided once, by someone other than its maker (PITFALLS 5)' USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    IF (NEW.status = 'APPLIED' AND NEW.fallback_date <> (NEW.decided_at AT TIME ZONE 'Europe/Athens')::date)
                       OR (NEW.status = 'REJECTED' AND NEW.fallback_date <> OLD.fallback_date) THEN
                        RAISE EXCEPTION 'the fall-back business date is derived from approval in Athens (REQ-PFC-213)' USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_fallback_request_guard BEFORE UPDATE ON pfc.fallback_request
                    FOR EACH ROW EXECUTE FUNCTION pfc.guard_fallback_request()
                """);
            // Deferred: the owner's successful command audit is appended after the handler's saves, in the same transaction.
            // PLT owns all access to approval/audit tables; PFC receives only a boolean proof.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION pfc.verify_fallback_execution() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    request pfc.fallback_request%ROWTYPE;
                    product_line text;
                BEGIN
                    SELECT * INTO request FROM pfc.fallback_request WHERE fallback_id = NEW.fallback_id;
                    IF request.status = 'APPLIED' THEN
                        SELECT line_code INTO product_line FROM pfc.product WHERE product_id = request.product_id;
                        IF NOT plt.pfc_fallback_approval_verified(
                            request.approval_request_id, request.legal_entity_id, request.fallback_id,
                            request.payload_hash::text, request.decided_by, product_line, request.jurisdiction,
                            request.decided_at, array_remove(ARRAY[request.requested_by, request.requested_by_principal], NULL)) THEN
                            RAISE EXCEPTION 'product fall-back execution requires its frozen, audited PLT approval (REQ-PFC-213)'
                                USING ERRCODE = 'integrity_constraint_violation';
                        END IF;
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER trg_fallback_execution_proof AFTER INSERT OR UPDATE ON pfc.fallback_request
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pfc.verify_fallback_execution();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_fallback_execution_proof ON pfc.fallback_request");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS pfc.verify_fallback_execution()");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_fallback_request_guard ON pfc.fallback_request");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS pfc.guard_fallback_request()");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_locked_version_guard ON pfc.product_version");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS pfc.guard_locked_version()");

            migrationBuilder.DropForeignKey(
                name: "fk_product_version_fallback_of",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropForeignKey(
                name: "fk_product_version_replaces",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropTable(
                name: "fallback_request",
                schema: "pfc");

            migrationBuilder.DropIndex(
                name: "IX_product_version_fallback_of_version_id",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropIndex(
                name: "IX_product_version_replaces_version_id",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_version_fallback_pair",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropColumn(
                name: "fallback_of_version_id",
                schema: "pfc",
                table: "product_version");

            migrationBuilder.DropColumn(
                name: "replaces_version_id",
                schema: "pfc",
                table: "product_version");
        }
    }
}
