namespace CoreIns.Modules.Claims.Persistence;

/// <summary>
/// Database guards of the claim financial ledger (D-ARC-34, docs/module-pattern.md §10; PRD-07 §7.1 "immutable after
/// Approved except status progression"):
/// <list type="bullet">
/// <item><c>financial_transaction</c> is append-only for every role, the owner included: UPDATE, DELETE and TRUNCATE are
/// refused and logged as a SECURITY line;</item>
/// <item>seal: a transaction (and a claim payment) can be inserted only by the database transaction that created its set
/// (<c>transaction_set.created_txid = txid_current()</c>, the top-level id, so EF savepoints are fine) while the set is
/// still Draft, so nothing can be appended to a submitted or approved set later, even balanced;</item>
/// <item><c>transaction_set</c>: identity, content and basis hashes are frozen; the approval binding and approval instant
/// are frozen once written; the status only moves forward (Draft → Submitted/PendingApproval/Approved/Rejected,
/// PendingApproval → Approved/Rejected, Approved → Posted);</item>
/// <item><c>claim_payment</c> money, payee and content-hash columns and its disbursement id once set are frozen;
/// <c>reserve_line</c> keys are frozen; nothing is deleted.</item>
/// </list>
/// </summary>
internal static class ClaimsFinancialSql
{
    public const string Up = """
        CREATE FUNCTION clm.reject_ledger_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            RAISE LOG 'SECURITY: % on append-only table clm.% refused for role % (D-ARC-34)', TG_OP, TG_TABLE_NAME, current_user;
            RAISE EXCEPTION 'clm.% is append-only: % is refused (D-ARC-34)', TG_TABLE_NAME, TG_OP USING ERRCODE = 'restrict_violation';
        END
        $fn$;

        CREATE TRIGGER tr_financial_transaction_append_only BEFORE UPDATE OR DELETE ON clm.financial_transaction
            FOR EACH ROW EXECUTE FUNCTION clm.reject_ledger_change();
        CREATE TRIGGER tr_financial_transaction_no_truncate BEFORE TRUNCATE ON clm.financial_transaction
            FOR EACH STATEMENT EXECUTE FUNCTION clm.reject_ledger_change();
        CREATE TRIGGER tr_transaction_set_no_truncate BEFORE TRUNCATE ON clm.transaction_set
            FOR EACH STATEMENT EXECUTE FUNCTION clm.reject_ledger_change();
        CREATE TRIGGER tr_claim_payment_no_truncate BEFORE TRUNCATE ON clm.claim_payment
            FOR EACH STATEMENT EXECUTE FUNCTION clm.reject_ledger_change();
        CREATE TRIGGER tr_reserve_line_no_truncate BEFORE TRUNCATE ON clm.reserve_line
            FOR EACH STATEMENT EXECUTE FUNCTION clm.reject_ledger_change();

        CREATE FUNCTION clm.seal_to_set() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            header_txid bigint;
            header_status text;
        BEGIN
            SELECT created_txid, status INTO header_txid, header_status FROM clm.transaction_set WHERE set_id = NEW.set_id;
            IF header_txid IS NULL THEN
                RAISE EXCEPTION 'clm.% row for set % has no set', TG_TABLE_NAME, NEW.set_id USING ERRCODE = 'restrict_violation';
            END IF;
            IF header_txid <> txid_current() OR header_status <> 'DRAFT' THEN
                RAISE LOG 'SECURITY: row appended to sealed claim transaction set % in clm.% by role % (D-ARC-34)', NEW.set_id, TG_TABLE_NAME, current_user;
                RAISE EXCEPTION 'claim transaction set % is sealed: its rows are written only by the transaction that created it (D-ARC-34)', NEW.set_id
                    USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_financial_transaction_sealed BEFORE INSERT ON clm.financial_transaction
            FOR EACH ROW EXECUTE FUNCTION clm.seal_to_set();
        CREATE TRIGGER tr_claim_payment_sealed BEFORE INSERT ON clm.claim_payment
            FOR EACH ROW EXECUTE FUNCTION clm.seal_to_set();

        CREATE FUNCTION clm.guard_transaction_set() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'clm.transaction_set rows are never deleted' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.set_id, NEW.claim_id, NEW.legal_entity_id, NEW.jurisdiction, NEW.content_hash, NEW.basis_hash, NEW.created_at, NEW.created_by, NEW.created_txid)
               IS DISTINCT FROM
               (OLD.set_id, OLD.claim_id, OLD.legal_entity_id, OLD.jurisdiction, OLD.content_hash, OLD.basis_hash, OLD.created_at, OLD.created_by, OLD.created_txid) THEN
                RAISE EXCEPTION 'clm.transaction_set identity and content are frozen' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.approval_request_id IS NOT NULL AND (NEW.approval_request_id, NEW.approval_type, NEW.approval_subject_type, NEW.approval_subject_id,
                    NEW.approval_payload_hash, NEW.approval_authority_type, NEW.approval_authority_amount, NEW.approval_authority_cost_type)
                IS DISTINCT FROM (OLD.approval_request_id, OLD.approval_type, OLD.approval_subject_type, OLD.approval_subject_id,
                    OLD.approval_payload_hash, OLD.approval_authority_type, OLD.approval_authority_amount, OLD.approval_authority_cost_type) THEN
                RAISE EXCEPTION 'clm.transaction_set approval binding is frozen' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.approved_at IS NOT NULL AND NEW.approved_at IS DISTINCT FROM OLD.approved_at THEN
                RAISE EXCEPTION 'clm.transaction_set approval instant is frozen' USING ERRCODE = 'restrict_violation';
            END IF;
            IF NEW.status <> OLD.status AND NOT (
                   (OLD.status = 'DRAFT' AND NEW.status IN ('SUBMITTED', 'PENDING_APPROVAL', 'APPROVED', 'REJECTED'))
                OR (OLD.status = 'SUBMITTED' AND NEW.status IN ('PENDING_APPROVAL', 'APPROVED', 'REJECTED'))
                OR (OLD.status = 'PENDING_APPROVAL' AND NEW.status IN ('APPROVED', 'REJECTED'))
                OR (OLD.status = 'APPROVED' AND NEW.status = 'POSTED')) THEN
                RAISE LOG 'SECURITY: clm.transaction_set % status % -> % refused for role %', OLD.set_id, OLD.status, NEW.status, current_user;
                RAISE EXCEPTION 'clm.transaction_set status % cannot move to %', OLD.status, NEW.status USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_transaction_set_guard BEFORE UPDATE OR DELETE ON clm.transaction_set
            FOR EACH ROW EXECUTE FUNCTION clm.guard_transaction_set();

        CREATE FUNCTION clm.freeze_claim_payment() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'clm.claim_payment rows are never deleted' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.claim_payment_id, NEW.claim_id, NEW.set_id, NEW.exposure_id, NEW.payee_party_id, NEW.payee_account_id, NEW.method, NEW.payment_type,
                NEW.amount, NEW.currency, NEW.disbursement_content_hash, NEW.legal_entity_id, NEW.jurisdiction, NEW.created_at, NEW.created_by)
               IS DISTINCT FROM
               (OLD.claim_payment_id, OLD.claim_id, OLD.set_id, OLD.exposure_id, OLD.payee_party_id, OLD.payee_account_id, OLD.method, OLD.payment_type,
                OLD.amount, OLD.currency, OLD.disbursement_content_hash, OLD.legal_entity_id, OLD.jurisdiction, OLD.created_at, OLD.created_by)
               OR (OLD.disbursement_id IS NOT NULL AND NEW.disbursement_id IS DISTINCT FROM OLD.disbursement_id) THEN
                RAISE LOG 'SECURITY: change of frozen clm.claim_payment % columns refused for role %', OLD.claim_payment_id, current_user;
                RAISE EXCEPTION 'clm.claim_payment money, payee and disbursement are frozen once written' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_claim_payment_frozen BEFORE UPDATE OR DELETE ON clm.claim_payment
            FOR EACH ROW EXECUTE FUNCTION clm.freeze_claim_payment();

        CREATE FUNCTION clm.freeze_reserve_line() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'clm.reserve_line rows are never deleted' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.reserve_line_id, NEW.claim_id, NEW.exposure_id, NEW.cost_type, NEW.cost_category, NEW.currency, NEW.legal_entity_id, NEW.created_at, NEW.created_by)
               IS DISTINCT FROM
               (OLD.reserve_line_id, OLD.claim_id, OLD.exposure_id, OLD.cost_type, OLD.cost_category, OLD.currency, OLD.legal_entity_id, OLD.created_at, OLD.created_by) THEN
                RAISE EXCEPTION 'clm.reserve_line keys are frozen' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_reserve_line_frozen BEFORE UPDATE OR DELETE ON clm.reserve_line
            FOR EACH ROW EXECUTE FUNCTION clm.freeze_reserve_line();

        CREATE TRIGGER tr_payee_account_view_no_delete BEFORE DELETE ON clm.payee_account_view
            FOR EACH ROW EXECUTE FUNCTION clm.reject_ledger_change();
        """;

    /// <summary>
    /// D-SL2-13: a set's approval requests are written in its submitting transaction while the set moves to PendingApproval;
    /// their binding (request, set, subject, hash, authority) is frozen, the status only moves Pending → Approved/Rejected,
    /// and nothing is deleted.
    /// </summary>
    public const string SetApprovalsUp = """
        CREATE FUNCTION clm.guard_set_approval() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'clm.set_approval rows are never deleted' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.approval_request_id, NEW.set_id, NEW.approval_type, NEW.subject_type, NEW.subject_id, NEW.payload_hash, NEW.authority_type,
                NEW.authority_cost_type, NEW.authority_amount, NEW.currency, NEW.legal_entity_id, NEW.created_at, NEW.created_by)
               IS DISTINCT FROM
               (OLD.approval_request_id, OLD.set_id, OLD.approval_type, OLD.subject_type, OLD.subject_id, OLD.payload_hash, OLD.authority_type,
                OLD.authority_cost_type, OLD.authority_amount, OLD.currency, OLD.legal_entity_id, OLD.created_at, OLD.created_by)
               OR (OLD.status <> 'PENDING' AND NEW.status IS DISTINCT FROM OLD.status) THEN
                RAISE LOG 'SECURITY: change of frozen clm.set_approval % refused for role %', OLD.approval_request_id, current_user;
                RAISE EXCEPTION 'clm.set_approval binding is frozen and its decision is final' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_set_approval_guard BEFORE UPDATE OR DELETE ON clm.set_approval
            FOR EACH ROW EXECUTE FUNCTION clm.guard_set_approval();
        CREATE TRIGGER tr_set_approval_no_truncate BEFORE TRUNCATE ON clm.set_approval
            FOR EACH STATEMENT EXECUTE FUNCTION clm.reject_ledger_change();
        """;

    public const string SetApprovalsDown = "DROP FUNCTION IF EXISTS clm.guard_set_approval() CASCADE;";

    public const string Down = """
        DROP FUNCTION IF EXISTS clm.reject_ledger_change() CASCADE;
        DROP FUNCTION IF EXISTS clm.seal_to_set() CASCADE;
        DROP FUNCTION IF EXISTS clm.guard_transaction_set() CASCADE;
        DROP FUNCTION IF EXISTS clm.freeze_claim_payment() CASCADE;
        DROP FUNCTION IF EXISTS clm.freeze_reserve_line() CASCADE;
        """;
}
