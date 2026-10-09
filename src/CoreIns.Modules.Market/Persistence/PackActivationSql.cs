namespace CoreIns.Modules.Market.Persistence;

/// <summary>Frozen activation facts and deferred native PLT/audit proof. The shared application DB credential is a trusted producer.</summary>
internal static class PackActivationSql
{
    public const string Up = """
        CREATE UNIQUE INDEX ux_pack_activation_active ON mkt.pack_activation(legal_entity_id, pack_id) WHERE status = 'ACTIVE';
        CREATE UNIQUE INDEX ux_pack_activation_pending ON mkt.pack_activation(legal_entity_id, pack_id) WHERE status = 'PENDING_APPROVAL';
        CREATE FUNCTION mkt.guard_pack_activation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF TG_OP = 'INSERT' THEN
            IF NEW.requested_by = 'system:genesis' AND NEW.decided_by = 'system:genesis' AND NEW.status = 'ACTIVE'
              AND NEW.content_hash IS NULL AND NEW.approval_request_id IS NULL AND NEW.supersedes_id IS NULL
              AND EXISTS (SELECT 1 FROM mkt.config_state s WHERE s.hash = NEW.resulting_hash AND s.cause = 'GENESIS'
                AND s.activated_at = NEW.activated_at AND s.manifest->'packVersions' @> jsonb_build_array(jsonb_build_object('packId',NEW.pack_id,'version',NEW.version))) THEN
              RETURN NEW;
            END IF;
            IF NEW.status <> 'PENDING_APPROVAL' THEN
              RAISE EXCEPTION 'new activation requires pending native approval' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF NEW.content_hash IS NULL OR NEW.parent_hash IS NULL OR NEW.target_digest IS NULL OR NEW.from_version IS NULL
              OR NEW.supersedes_id IS NULL OR NEW.approval_request_id IS NULL OR length(btrim(NEW.reason)) < 20
              OR NEW.decided_by IS NOT NULL OR NEW.activated_at IS NOT NULL OR NEW.resulting_hash IS NOT NULL
              OR NEW.window_from IS NOT NULL OR NEW.window_to IS NOT NULL OR cardinality(NEW.hashes_issued) <> 0 THEN
              RAISE EXCEPTION 'pending activation requires complete frozen facts only' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
          ELSE
            IF (to_jsonb(NEW) - ARRAY['status','decided_by','activated_at','resulting_hash','window_from','window_to','hashes_issued','decision_reason','record_version'])
               IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status','decided_by','activated_at','resulting_hash','window_from','window_to','hashes_issued','decision_reason','record_version']) THEN
              RAISE EXCEPTION 'activation request facts are immutable' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF OLD.status = 'ACTIVE' THEN
              IF NEW.status <> 'SUPERSEDED' OR (to_jsonb(NEW) - ARRAY['status','record_version']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status','record_version']) THEN
                RAISE EXCEPTION 'active history is frozen' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
            ELSIF OLD.status = 'PENDING_APPROVAL' THEN
              IF NEW.status NOT IN ('ACTIVE','REJECTED') OR NEW.decided_by IS NULL OR NEW.decision_reason IS NULL
                OR NEW.decided_by = OLD.requested_by OR NEW.decided_by IS NOT DISTINCT FROM OLD.requested_principal THEN
                RAISE EXCEPTION 'decision requires a distinct checker' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
              IF NEW.status = 'REJECTED' AND (NEW.activated_at IS NOT NULL OR NEW.resulting_hash IS NOT NULL OR NEW.window_from IS NOT NULL OR cardinality(NEW.hashes_issued) <> 0) THEN
                RAISE EXCEPTION 'rejection cannot apply state' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
            ELSE
              RAISE EXCEPTION 'decided activation is final' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
            IF NEW.record_version <> OLD.record_version + 1 THEN
              RAISE EXCEPTION 'record version must advance once' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER tr_pack_activation_frozen BEFORE INSERT OR UPDATE ON mkt.pack_activation
          FOR EACH ROW EXECUTE FUNCTION mkt.guard_pack_activation();

        CREATE FUNCTION mkt.verify_pack_activation_execution() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE r mkt.pack_activation%ROWTYPE; entity_code text; state mkt.config_state%ROWTYPE; source mkt.pack_activation%ROWTYPE;
        BEGIN
          SELECT * INTO r FROM mkt.pack_activation WHERE id = NEW.id;
          -- Historical/genesis activations have no invented approval proof; they may only be superseded.
          IF r.content_hash IS NOT NULL AND r.status IN ('ACTIVE','SUPERSEDED') THEN
            SELECT code INTO entity_code FROM mkt.legal_entity WHERE legal_entity_id = r.legal_entity_id;
            SELECT * INTO state FROM mkt.config_state WHERE hash = r.resulting_hash;
            SELECT * INTO source FROM mkt.pack_activation WHERE id = r.supersedes_id;
            IF NOT FOUND OR state.hash IS NULL OR r.activated_at IS NULL OR r.resulting_hash IS NULL OR source.status <> 'SUPERSEDED' OR source.version <> r.from_version
              OR state.cause_ref IS DISTINCT FROM r.id OR state.parent_hash IS DISTINCT FROM r.parent_hash
              OR state.activated_at IS DISTINCT FROM r.activated_at
              OR state.cause <> CASE WHEN r.kind = 'ROLLBACK' THEN 'PACK_ROLLBACK' ELSE 'PACK_ACTIVATION' END
              OR r.window_from IS NULL OR (r.kind = 'ROLLBACK' AND (r.window_from <> source.activated_at OR r.window_to IS DISTINCT FROM r.activated_at))
              OR NOT plt.market_activation_approval_verified(r.approval_request_id, r.legal_entity_id, r.id, r.content_hash::text,
                r.decided_by, r.pack_id, entity_code, r.activated_at, array_remove(ARRAY[r.requested_by,r.requested_principal],NULL)) THEN
              RAISE EXCEPTION 'activation requires frozen state and audited native PLT approval' USING ERRCODE = 'integrity_constraint_violation';
            END IF;
          END IF;
          RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER tr_pack_activation_proof AFTER INSERT OR UPDATE ON mkt.pack_activation
          DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION mkt.verify_pack_activation_execution();
        """;

    public const string Down = """
        DROP TRIGGER tr_pack_activation_proof ON mkt.pack_activation;
        DROP FUNCTION mkt.verify_pack_activation_execution();
        DROP TRIGGER tr_pack_activation_frozen ON mkt.pack_activation;
        DROP FUNCTION mkt.guard_pack_activation();
        DROP INDEX mkt.ux_pack_activation_active;
        DROP INDEX mkt.ux_pack_activation_pending;
        """;
}
