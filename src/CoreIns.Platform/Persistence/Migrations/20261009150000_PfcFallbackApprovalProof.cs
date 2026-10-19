using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoreIns.Platform.Persistence.Migrations;

/// <summary>PLT-owned proof consistency for product fallback; the shared application database role remains a trusted producer.</summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20261009150000_PfcFallbackApprovalProof")]
public sealed class PfcFallbackApprovalProof : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION plt.pfc_fallback_approval_verified(
            p_request uuid, p_entity uuid, p_fallback uuid, p_hash text, p_checker text,
            p_product_line text, p_jurisdiction text, p_decided_at timestamptz, p_participants text[])
        RETURNS boolean LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, plt AS $$
          SELECT EXISTS (
            SELECT 1 FROM plt.approval_request r
            JOIN plt.audit_event a ON a.object_id = r.request_id::text
              AND a.object_module = 'PLT' AND a.object_type = 'ApprovalRequest'
              AND a.operation = 'plt.Approval.decide' AND a.outcome = 'Succeeded'
              AND a.legal_entity = r.legal_entity AND a.authority_check_id = r.authority_check_id
              AND a.actor_kind = r.checker_kind AND a.actor_id = r.checker_id
            JOIN plt.audit_event owner ON owner.object_id = p_fallback::text
              AND owner.object_module = 'PFC' AND owner.object_type = 'ProductFallback'
              AND owner.operation = 'pfc.ProductVersion.decideFallback' AND owner.outcome = 'Succeeded'
              AND owner.legal_entity = r.legal_entity
            WHERE r.request_id = p_request AND r.approval_type = 'PFC.Fallback'
              AND r.object_module = 'PFC' AND r.object_type = 'ProductFallback' AND r.object_id = p_fallback::text
              AND r.diff->>'legalEntityId' = p_entity::text AND r.payload_hash = p_hash
              AND r.status = 'Approved' AND r.decision = 'Approved' AND r.checker_kind = 'USER'
              AND r.checker_kind || ':' || r.checker_id = p_checker AND r.decided_at = p_decided_at
              AND r.authority_type = 'PFC.EMERGENCY_CHANGE'
              AND r.authority_codes->>'productLine' = p_product_line
              AND r.authority_codes->>'jurisdiction' = p_jurisdiction
              AND a.authority_used LIKE 'PFC.EMERGENCY_CHANGE:Allow:%'
              AND NOT (p_checker = ANY(p_participants)) AND NOT (p_checker = ANY(r.editors))
              AND p_checker <> r.maker_kind || ':' || r.maker_id
              AND (r.maker_on_behalf_of_id IS NULL OR p_checker <> r.maker_on_behalf_of_kind || ':' || r.maker_on_behalf_of_id)
              AND (a.on_behalf_of IS NULL OR (
                NOT (a.on_behalf_of = ANY(p_participants)) AND NOT (a.on_behalf_of = ANY(r.editors))
                AND a.on_behalf_of <> r.maker_kind || ':' || r.maker_id
                AND (r.maker_on_behalf_of_id IS NULL OR a.on_behalf_of <> r.maker_on_behalf_of_kind || ':' || r.maker_on_behalf_of_id)))
          );
        $$;
        REVOKE ALL ON FUNCTION plt.pfc_fallback_approval_verified(uuid, uuid, uuid, text, text, text, text, timestamptz, text[]) FROM PUBLIC;
        """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "DROP FUNCTION plt.pfc_fallback_approval_verified(uuid, uuid, uuid, text, text, text, text, timestamptz, text[]);");
}
