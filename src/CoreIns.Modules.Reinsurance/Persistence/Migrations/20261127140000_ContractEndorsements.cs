using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreIns.Modules.Reinsurance.Persistence.Migrations
{
    /// <summary>
    /// BO-RI-03: contract endorsements. An endorsement is a further <c>ri.contract_version</c> row (version 2 and up) with its
    /// own valid-from date, record time and PLT approval; the approved rows stay untouched. The per-contract exclusion on
    /// the valid period goes (the version in force is the highest approved version whose valid-from has been reached); the
    /// version guard lets an endorsement be approved while its contract is ACTIVE, and the commit-time proof checks an
    /// endorsement against its own approval request.
    /// </summary>
    [DbContext(typeof(ReinsuranceDbContext))]
    [Migration("20261127140000_ContractEndorsements")]
    public sealed class ContractEndorsements : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "endorsement_status", schema: "ri", table: "contract_version", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "endorsement_reason", schema: "ri", table: "contract_version", type: "text", nullable: true);
            migrationBuilder.AddCheckConstraint(
                name: "ck_contract_version_endorsement",
                schema: "ri",
                table: "contract_version",
                sql: "(version_no = 1) = (endorsement_status IS NULL) AND (endorsement_status IS NULL OR endorsement_status IN ('PENDING', 'APPROVED', 'REJECTED'))");

            migrationBuilder.Sql("""
                ALTER TABLE ri.contract_version DROP CONSTRAINT ex_contract_version_period;

                CREATE OR REPLACE FUNCTION ri.contract_version_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.approved_at IS NOT NULL OR NEW.approved_by IS NOT NULL THEN
                            RAISE EXCEPTION 'ri.contract_version must start unapproved' USING ERRCODE = '42501';
                        END IF;
                        IF NEW.version_no > 1 AND NEW.endorsement_status IS DISTINCT FROM 'PENDING' THEN
                            RAISE EXCEPTION 'an endorsement version must start PENDING' USING ERRCODE = '42501';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'ri.contract_version rows are never deleted (REQ-RI-065)' USING ERRCODE = '42501';
                    END IF;
                    IF OLD.approved_at IS NOT NULL THEN
                        RAISE EXCEPTION 'ri.contract_version % is approved and immutable (REQ-RI-057)', OLD.version_id USING ERRCODE = '42501';
                    END IF;
                    IF NEW.version_id IS DISTINCT FROM OLD.version_id
                       OR NEW.contract_id IS DISTINCT FROM OLD.contract_id
                       OR NEW.version_no IS DISTINCT FROM OLD.version_no
                       OR NEW.known_from IS DISTINCT FROM OLD.known_from
                       OR NEW.created_at IS DISTINCT FROM OLD.created_at
                       OR NEW.created_by IS DISTINCT FROM OLD.created_by
                       OR NEW.endorsement_reason IS DISTINCT FROM OLD.endorsement_reason THEN
                        RAISE EXCEPTION 'ri.contract_version identity columns are immutable' USING ERRCODE = '42501';
                    END IF;
                    IF OLD.version_no > 1 AND NEW.endorsement_status IS DISTINCT FROM OLD.endorsement_status AND NOT (
                           OLD.endorsement_status = 'PENDING' AND NEW.endorsement_status IN ('APPROVED', 'REJECTED')) THEN
                        RAISE EXCEPTION 'ri.contract_version endorsement cannot move from % to %', OLD.endorsement_status, NEW.endorsement_status USING ERRCODE = '23514';
                    END IF;
                    IF NEW.approved_at IS NOT NULL THEN
                        IF NEW.valid_from IS DISTINCT FROM OLD.valid_from
                           OR NEW.valid_to IS DISTINCT FROM OLD.valid_to
                           OR NEW.placed_pct IS DISTINCT FROM OLD.placed_pct
                           OR NEW.content_hash IS DISTINCT FROM OLD.content_hash
                           OR NEW.content_rev IS DISTINCT FROM OLD.content_rev
                           OR NEW.known_to IS DISTINCT FROM OLD.known_to
                           OR NEW.approval_request_id IS DISTINCT FROM OLD.approval_request_id THEN
                            RAISE EXCEPTION 'approving ri.contract_version % must not change the submitted content', OLD.version_id USING ERRCODE = '42501';
                        END IF;
                        IF NEW.version_no > 1 AND NEW.endorsement_status IS DISTINCT FROM 'APPROVED' THEN
                            RAISE EXCEPTION 'an approved endorsement must be marked APPROVED' USING ERRCODE = '23514';
                        END IF;
                        PERFORM 1 FROM ri.contract c
                         WHERE c.contract_id = NEW.contract_id
                           AND (c.status = 'PENDING_APPROVAL' OR (NEW.version_no > 1 AND c.status = 'ACTIVE'))
                           FOR SHARE;
                        IF NOT FOUND THEN
                            RAISE EXCEPTION 'ri.contract_version % can be approved only while its contract is pending approval (an endorsement: while it is active)', OLD.version_id USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $$;

                CREATE OR REPLACE FUNCTION ri.approval_execution_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                    header RECORD;
                    version RECORD;
                BEGIN
                    IF TG_TABLE_NAME = 'contract_version' THEN
                        IF NEW.approved_at IS NULL THEN RETURN NEW; END IF;
                        SELECT * INTO header FROM ri.contract WHERE contract_id = NEW.contract_id;
                        IF NEW.version_no > 1 THEN
                            -- An endorsement is proven by its own request: the maker is the endorser, never the checker.
                            IF NEW.approval_request_id IS NULL
                               OR NOT plt.ri_contract_approval_verified(NEW.approval_request_id, header.legal_entity_id,
                                    header.contract_id, NEW.content_hash, NEW.approved_by, header.contract_type,
                                    header.participants || ARRAY[header.submitted_by, header.created_by, NEW.created_by]) THEN
                                RAISE EXCEPTION 'ri contract endorsement requires its frozen, independently checked PLT decision and audit'
                                    USING ERRCODE = '42501';
                            END IF;
                            RETURN NEW;
                        END IF;
                        version := NEW;
                    ELSE
                        IF NEW.status NOT IN ('APPROVED', 'ACTIVE', 'EXPIRED') THEN RETURN NEW; END IF;
                        header := NEW;
                        SELECT * INTO version FROM ri.contract_version
                            WHERE contract_id = NEW.contract_id AND version_no = 1 AND known_to IS NULL AND approved_at IS NOT NULL;
                    END IF;
                    IF version.version_id IS NULL OR header.approval_request_id IS DISTINCT FROM version.approval_request_id
                       OR (header.status IN ('APPROVED', 'ACTIVE', 'EXPIRED') AND (
                            header.decided_by IS DISTINCT FROM version.approved_by OR header.decided_at IS DISTINCT FROM version.approved_at))
                       OR NOT plt.ri_contract_approval_verified(version.approval_request_id, header.legal_entity_id,
                            header.contract_id, version.content_hash, version.approved_by, header.contract_type,
                            header.participants || ARRAY[header.submitted_by, header.created_by]) THEN
                        RAISE EXCEPTION 'ri contract approval requires its frozen, independently checked PLT decision and audit'
                            USING ERRCODE = '42501';
                    END IF;
                    RETURN NEW;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Roll-forward only: the guards keep the endorsement-aware bodies (harmless without endorsement rows).
            migrationBuilder.DropCheckConstraint(name: "ck_contract_version_endorsement", schema: "ri", table: "contract_version");
            migrationBuilder.DropColumn(name: "endorsement_reason", schema: "ri", table: "contract_version");
            migrationBuilder.DropColumn(name: "endorsement_status", schema: "ri", table: "contract_version");
        }
    }
}
