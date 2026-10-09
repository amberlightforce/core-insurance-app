using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoreIns.Platform.Persistence.Migrations;

/// <summary>PLT-owned proof consistency for Market activation; the shared application database role remains a trusted producer.</summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20261009160000_MarketActivationApprovalProof")]
public sealed class MarketActivationApprovalProof : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION plt.market_activation_approval_verified(
            p_request uuid, p_entity uuid, p_activation uuid, p_hash text, p_checker text,
            p_pack text, p_entity_code text, p_decided_at timestamptz, p_participants text[])
        RETURNS boolean LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, plt AS $$
          SELECT EXISTS (
            SELECT 1 FROM plt.approval_request r
            JOIN plt.audit_event a ON a.object_id = r.request_id::text
              AND a.object_module = 'PLT' AND a.object_type = 'ApprovalRequest'
              AND a.operation = 'plt.Approval.decide' AND a.outcome = 'Succeeded'
              AND a.legal_entity = r.legal_entity AND a.authority_check_id = r.authority_check_id
              AND a.actor_kind = r.checker_kind AND a.actor_id = r.checker_id
            JOIN plt.audit_event owner ON owner.object_id = p_activation::text
              AND owner.object_module = 'MKT' AND owner.object_type = 'PackActivation'
              AND owner.operation = 'mkt.PackActivation.decide' AND owner.outcome = 'Succeeded'
              AND owner.legal_entity = r.legal_entity
            WHERE r.request_id = p_request AND r.approval_type = 'MKT.PackActivation'
              AND r.object_module = 'MKT' AND r.object_type = 'PackActivation' AND r.object_id = p_activation::text
              AND r.diff->>'legalEntityId' = p_entity::text AND r.payload_hash = p_hash
              AND r.status = 'Approved' AND r.decision = 'Approved' AND r.checker_kind = 'USER'
              AND r.checker_kind || ':' || r.checker_id = p_checker AND r.decided_at = p_decided_at
              AND r.authority_type = 'MKT_PACK_ACTIVATION'
              AND r.authority_codes->>'pack' = p_pack
              AND r.authority_codes->>'legalEntity' = p_entity_code
              AND a.authority_used LIKE 'MKT_PACK_ACTIVATION:Allow:%'
              AND NOT (p_checker = ANY(p_participants)) AND NOT (p_checker = ANY(r.editors))
              AND p_checker <> r.maker_kind || ':' || r.maker_id
              AND (r.maker_on_behalf_of_id IS NULL OR p_checker <> r.maker_on_behalf_of_kind || ':' || r.maker_on_behalf_of_id)
              AND (a.on_behalf_of IS NULL OR (
                NOT (a.on_behalf_of = ANY(p_participants)) AND NOT (a.on_behalf_of = ANY(r.editors))
                AND a.on_behalf_of <> r.maker_kind || ':' || r.maker_id
                AND (r.maker_on_behalf_of_id IS NULL OR a.on_behalf_of <> r.maker_on_behalf_of_kind || ':' || r.maker_on_behalf_of_id)))
          );
        $$;
        REVOKE ALL ON FUNCTION plt.market_activation_approval_verified(uuid, uuid, uuid, text, text, text, text, timestamptz, text[]) FROM PUBLIC;
        """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "DROP FUNCTION plt.market_activation_approval_verified(uuid, uuid, uuid, text, text, text, text, timestamptz, text[]);");
}
