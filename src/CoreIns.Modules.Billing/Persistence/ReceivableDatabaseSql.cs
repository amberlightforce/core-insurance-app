namespace CoreIns.Modules.Billing.Persistence;

/// <summary>Guards apply to every role, including the migration owner (D-ARC-34).</summary>
internal static class ReceivableDatabaseSql
{
    public const string Guards = """
        CREATE TRIGGER tr_receivable_append_only BEFORE UPDATE OR DELETE ON bil.receivable FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_receivable_no_truncate BEFORE TRUNCATE ON bil.receivable FOR EACH STATEMENT EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_receivable_allocation_append_only BEFORE UPDATE OR DELETE ON bil.receivable_allocation FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_receivable_allocation_no_truncate BEFORE TRUNCATE ON bil.receivable_allocation FOR EACH STATEMENT EXECUTE FUNCTION bil.reject_change();

        CREATE FUNCTION bil.guard_receivable_allocation() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE receipt_row bil.receipt; receivable_row bil.receivable; used numeric;
        BEGIN
            SELECT * INTO STRICT receipt_row FROM bil.receipt WHERE receipt_id = NEW.receipt_id FOR UPDATE;
            SELECT * INTO STRICT receivable_row FROM bil.receivable WHERE receivable_id = NEW.receivable_id FOR UPDATE;
            IF NEW.legal_entity_id <> receipt_row.legal_entity_id OR NEW.legal_entity_id <> receivable_row.legal_entity_id
               OR receipt_row.billing_account_id <> receivable_row.billing_account_id
               OR NEW.currency <> receipt_row.currency OR NEW.currency <> receivable_row.currency THEN
                RAISE EXCEPTION 'receivable allocation scope mismatch' USING ERRCODE = 'BL001';
            END IF;
            SELECT coalesce(sum(amount),0) INTO used FROM (
                SELECT amount FROM bil.receivable_allocation WHERE receipt_id = NEW.receipt_id
                UNION ALL SELECT amount FROM bil.allocation WHERE receipt_id = NEW.receipt_id
            ) a;
            IF used + NEW.amount > receipt_row.amount THEN
                RAISE EXCEPTION 'receipt allocation exceeds received cash' USING ERRCODE = 'BL001';
            END IF;
            SELECT coalesce(sum(amount),0) INTO used FROM bil.receivable_allocation WHERE receivable_id = NEW.receivable_id;
            IF used + NEW.amount > receivable_row.amount THEN
                RAISE EXCEPTION 'receivable allocation exceeds open item' USING ERRCODE = 'BL001';
            END IF;
            RETURN NEW;
        END
        $fn$;
        CREATE TRIGGER tr_receivable_allocation_guard BEFORE INSERT ON bil.receivable_allocation FOR EACH ROW EXECUTE FUNCTION bil.guard_receivable_allocation();

        -- Existing invoice allocation guard already locks the receipt first; add the complementary shared cash cap.
        CREATE FUNCTION bil.guard_shared_receipt() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE limit_amount numeric; used numeric;
        BEGIN
            SELECT amount INTO STRICT limit_amount FROM bil.receipt WHERE receipt_id = NEW.receipt_id FOR UPDATE;
            SELECT coalesce(sum(amount),0) INTO used FROM (
                SELECT amount FROM bil.allocation WHERE receipt_id = NEW.receipt_id
                UNION ALL SELECT amount FROM bil.receivable_allocation WHERE receipt_id = NEW.receipt_id
            ) a;
            IF used + NEW.amount > limit_amount THEN
                RAISE EXCEPTION 'receipt allocation exceeds received cash' USING ERRCODE = 'BL001';
            END IF;
            RETURN NEW;
        END
        $fn$;
        CREATE TRIGGER tr_allocation_shared_receipt BEFORE INSERT ON bil.allocation FOR EACH ROW EXECUTE FUNCTION bil.guard_shared_receipt();

        INSERT INTO bil.ledger_account (account_code, name_en, name_el, account_type, normal_balance)
        VALUES ('LA-28','Non-premium receivable','Απαιτήσεις ανακτήσεων ζημιών','ASSET','DEBIT'),
               ('LA-24','Friendly Settlement clearing','Εκκαθάριση φιλικού διακανονισμού','CLEARING','DEBIT')
        ON CONFLICT DO NOTHING;
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier, debit_account, credit_account, amount_expression)
        VALUES ('BLR-RECV-CLM',1,'RECEIVABLE_REGISTERED','*','DIRECT_BILL','*','CLM_CLAIM_PAYMENT','LA-28','LA-17','AMOUNT'),
               ('BLR-RECV-FS',1,'RECEIVABLE_REGISTERED','*','DIRECT_BILL','*','FS_CLEARING','LA-28','LA-24','AMOUNT'),
               ('BLR-COLLECT-CLM',1,'RECEIVABLE_COLLECTED','*','DIRECT_BILL','*','CLM_CLAIM_PAYMENT','LA-11','LA-28','AMOUNT'),
               ('BLR-COLLECT-FS',1,'RECEIVABLE_COLLECTED','*','DIRECT_BILL','*','FS_CLEARING','LA-11','LA-28','AMOUNT');
        """;
}

