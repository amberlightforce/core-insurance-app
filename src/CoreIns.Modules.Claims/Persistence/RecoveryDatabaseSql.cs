namespace CoreIns.Modules.Claims.Persistence;

/// <summary>Additive slice-4 guards; existing append-only and same-transaction ledger seals remain installed.</summary>
internal static class RecoveryDatabaseSql
{
    public const string Up = """
        CREATE FUNCTION clm.guard_recovery_financial_links() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF NEW.kind = 'PAYMENT' AND NEW.amount < 0 THEN
                IF NOT EXISTS (SELECT 1 FROM clm.financial_transaction t WHERE t.txn_id = NEW.reverses_txn_id
                    AND t.kind = 'PAYMENT' AND t.amount = -NEW.amount AND t.claim_id = NEW.claim_id
                    AND t.legal_entity_id = NEW.legal_entity_id AND t.exposure_id = NEW.exposure_id AND t.currency = NEW.currency)
                    OR NOT EXISTS (SELECT 1 FROM clm.transaction_set s WHERE s.set_id = NEW.set_id
                        AND s.evidence_kind = 'DISBURSEMENT_OUTCOME' AND s.evidence_ref IS NOT NULL) THEN
                    RAISE EXCEPTION 'Payment reversal needs linked original and disbursement outcome evidence' USING ERRCODE = 'check_violation';
                END IF;
            END IF;
            IF NEW.kind IN ('RECOVERY_RESERVE','RECOVERY') THEN
                IF NOT EXISTS (SELECT 1 FROM clm.recovery r JOIN clm.reserve_line l ON l.reserve_line_id = NEW.reserve_line_id
                    WHERE r.recovery_id = NEW.recovery_id AND r.claim_id = NEW.claim_id AND r.legal_entity_id = NEW.legal_entity_id
                      AND (r.exposure_id IS NULL OR r.exposure_id = NEW.exposure_id)
                      AND l.claim_id = NEW.claim_id AND l.exposure_id = NEW.exposure_id AND l.legal_entity_id = NEW.legal_entity_id
                      AND r.currency = NEW.currency AND l.currency = NEW.currency) THEN
                    RAISE EXCEPTION 'Recovery transaction links do not identify one claim, entity and exposure' USING ERRCODE = 'check_violation';
                END IF;
                IF NEW.kind = 'RECOVERY' AND NOT EXISTS (SELECT 1 FROM clm.transaction_set s WHERE s.set_id = NEW.set_id
                    AND s.evidence_kind IN ('BIL_ALLOCATION','FS_STATEMENT_LINE') AND s.evidence_ref IS NOT NULL
                    AND s.created_by = 'SERVICE:clm-financial-engine') THEN
                    RAISE EXCEPTION 'Received recovery needs an evidence-backed system set' USING ERRCODE = 'check_violation';
                END IF;
            ELSIF NEW.recovery_id IS NOT NULL THEN
                RAISE EXCEPTION 'A non-recovery transaction cannot carry a recovery id' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;
        CREATE TRIGGER tr_recovery_financial_links BEFORE INSERT ON clm.financial_transaction
            FOR EACH ROW EXECUTE FUNCTION clm.guard_recovery_financial_links();

        CREATE FUNCTION clm.freeze_system_evidence() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.evidence_kind, NEW.evidence_ref) IS DISTINCT FROM (OLD.evidence_kind, OLD.evidence_ref) THEN
                RAISE EXCEPTION 'System-set evidence is frozen at creation' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;
        CREATE TRIGGER tr_system_evidence_frozen BEFORE UPDATE ON clm.transaction_set
            FOR EACH ROW EXECUTE FUNCTION clm.freeze_system_evidence();

        CREATE FUNCTION clm.freeze_payment_correction_links() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.reissue_of, NEW.reversal_of, NEW.counterparty_insurer_party_id) IS DISTINCT FROM (OLD.reissue_of, OLD.reversal_of, OLD.counterparty_insurer_party_id)
                OR (OLD.fs_statement_id IS NOT NULL AND NEW.fs_statement_id IS DISTINCT FROM OLD.fs_statement_id)
                OR (OLD.fiscal_mark IS NOT NULL AND NEW.fiscal_mark IS DISTINCT FROM OLD.fiscal_mark) THEN
                RAISE EXCEPTION 'Payment correction and established settlement evidence are frozen' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;
        CREATE TRIGGER tr_payment_correction_links_frozen BEFORE UPDATE ON clm.claim_payment
            FOR EACH ROW EXECUTE FUNCTION clm.freeze_payment_correction_links();

        CREATE FUNCTION clm.guard_recovery_case_links() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM clm.claim c WHERE c.claim_id = NEW.claim_id AND c.legal_entity_id = NEW.legal_entity_id)
                OR (NEW.exposure_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM clm.exposure e WHERE e.exposure_id = NEW.exposure_id
                    AND e.claim_id = NEW.claim_id AND e.legal_entity_id = NEW.legal_entity_id)) THEN
                RAISE EXCEPTION 'Recovery case claim/entity/exposure mismatch' USING ERRCODE = 'check_violation';
            END IF;
            IF TG_OP = 'UPDATE' AND (NEW.recovery_id, NEW.claim_id, NEW.exposure_id, NEW.type, NEW.counterparty_party_id,
                NEW.currency, NEW.legal_entity_id, NEW.jurisdiction, NEW.created_at, NEW.created_by)
                IS DISTINCT FROM (OLD.recovery_id, OLD.claim_id, OLD.exposure_id, OLD.type, OLD.counterparty_party_id,
                OLD.currency, OLD.legal_entity_id, OLD.jurisdiction, OLD.created_at, OLD.created_by) THEN
                RAISE EXCEPTION 'Recovery case financial identity is frozen' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;
        CREATE TRIGGER tr_recovery_case_links BEFORE INSERT OR UPDATE ON clm.recovery
            FOR EACH ROW EXECUTE FUNCTION clm.guard_recovery_case_links();
        """;

    public const string Down = """
        DROP FUNCTION IF EXISTS clm.guard_recovery_financial_links() CASCADE;
        DROP FUNCTION IF EXISTS clm.freeze_system_evidence() CASCADE;
        DROP FUNCTION IF EXISTS clm.freeze_payment_correction_links() CASCADE;
        DROP FUNCTION IF EXISTS clm.guard_recovery_case_links() CASCADE;
        """;
}
