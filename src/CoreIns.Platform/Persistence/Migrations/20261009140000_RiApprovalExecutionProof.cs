using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoreIns.Platform.Persistence.Migrations;

/// <summary>PLT-owned database SPI: verifies a frozen, audited RI approval without exposing platform tables.</summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20261009140000_RiApprovalExecutionProof")]
public sealed class RiApprovalExecutionProof : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION plt.ri_contract_approval_verified(
            p_request uuid, p_entity uuid, p_contract uuid, p_hash text, p_checker text,
            p_contract_type text, p_participants text[])
        RETURNS boolean LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, plt AS $$
          SELECT EXISTS (
            SELECT 1 FROM plt.approval_request r
            JOIN plt.audit_event a ON a.object_id = r.request_id::text
              AND a.object_module = 'PLT' AND a.object_type = 'ApprovalRequest'
              AND a.operation = 'plt.Approval.decide' AND a.outcome = 'Succeeded'
              AND a.legal_entity = r.legal_entity AND a.authority_check_id = r.authority_check_id
              AND a.actor_kind = r.checker_kind AND a.actor_id = r.checker_id
            WHERE r.request_id = p_request AND r.approval_type = 'RI.CONTRACT_APPROVE'
              AND r.object_module = 'RI' AND r.object_type = 'Contract' AND r.object_id = p_contract::text
              AND r.diff->>'legalEntityId' = p_entity::text AND r.payload_hash = p_hash
              AND r.status = 'Approved' AND r.decision = 'Approved' AND r.checker_kind = 'USER'
              AND r.checker_kind || ':' || r.checker_id = p_checker
              AND r.authority_type = 'RI.CONTRACT_APPROVE'
              AND r.authority_codes->>'contractType' = p_contract_type
              AND a.authority_used LIKE 'RI.CONTRACT_APPROVE:Allow:%'
              AND NOT (p_checker = ANY(p_participants))
              AND NOT (p_checker = ANY(r.editors))
              AND p_checker <> r.maker_kind || ':' || r.maker_id
              AND (r.maker_on_behalf_of_id IS NULL OR p_checker <> r.maker_on_behalf_of_kind || ':' || r.maker_on_behalf_of_id)
              AND (a.on_behalf_of IS NULL OR (
                NOT (a.on_behalf_of = ANY(p_participants)) AND NOT (a.on_behalf_of = ANY(r.editors))
                AND a.on_behalf_of <> r.maker_kind || ':' || r.maker_id
                AND (r.maker_on_behalf_of_id IS NULL OR a.on_behalf_of <> r.maker_on_behalf_of_kind || ':' || r.maker_on_behalf_of_id)))
          );
        $$;
        REVOKE ALL ON FUNCTION plt.ri_contract_approval_verified(uuid, uuid, uuid, text, text, text, text[]) FROM PUBLIC;
        """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "DROP FUNCTION plt.ri_contract_approval_verified(uuid, uuid, uuid, text, text, text, text[]);");
}
