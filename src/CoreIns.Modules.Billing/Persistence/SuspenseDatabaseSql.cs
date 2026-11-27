namespace CoreIns.Modules.Billing.Persistence;

/// <summary>BO-BIL-08: ledger rules of suspense moves, the receipt freeze relaxed for transfers, and the backfill of the items.</summary>
internal static class SuspenseDatabaseSql
{
    public const string Up = """
        INSERT INTO bil.ledger_account (account_code, name_en, name_el, account_type, normal_balance)
        VALUES ('LA-29', 'Suspense transfer clearing', 'Εκκαθάριση μεταφοράς αδιάθετων εισπράξεων ‡', 'CLEARING', 'EITHER')
        ON CONFLICT DO NOTHING;

        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-SUSPENSE-XFER-OUT', 1, 'SUSPENSE_TRANSFERRED', '*', 'DIRECT_BILL', '*', 'OUT', 'LA-11', 'LA-29', 'AMOUNT', DATE '2000-01-01', NULL, 'BO-BIL-08 (unapplied cash leaves the source account)'),
            ('BLR-SUSPENSE-XFER-IN', 1, 'SUSPENSE_TRANSFERRED', '*', 'DIRECT_BILL', '*', 'IN', 'LA-29', 'LA-11', 'AMOUNT', DATE '2000-01-01', NULL, 'BO-BIL-08 (unapplied cash arrives on the target account)'),
            ('BLR-SUSPENSE-REFUND', 1, 'SUSPENSE_REFUND_APPROVED', '*', '*', '*', '*', 'LA-11', 'LA-12', 'AMOUNT', DATE '2000-01-01', NULL, 'BO-BIL-08 (refund to sender approved: unapplied cash to refunds payable)');

        -- The money and number columns stay frozen; the account of a receipt moves only while it is in suspense (bil.Suspense.transfer).
        CREATE OR REPLACE FUNCTION bil.freeze_receipt() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.amount, NEW.currency, NEW.receipt_number, NEW.value_date) IS DISTINCT FROM (OLD.amount, OLD.currency, OLD.receipt_number, OLD.value_date) THEN
                RAISE EXCEPTION 'bil.receipt amounts and numbers are frozen once written' USING ERRCODE = 'BL004';
            END IF;
            IF NEW.billing_account_id IS DISTINCT FROM OLD.billing_account_id AND NOT (OLD.state = 'SUSPENSE' AND NEW.state = 'SUSPENSE') THEN
                RAISE EXCEPTION 'the account of a receipt moves only while it is in suspense' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        INSERT INTO bil.suspense_item (suspense_item_id, legal_entity_id, receipt_id, billing_account_id, amount, currency, state, reason, created_at, record_version)
        SELECT gen_random_uuid(), r.legal_entity_id, r.receipt_id, r.billing_account_id, r.amount, r.currency, 'OPEN', r.suspense_reason, r.recorded_at, 1
        FROM bil.receipt r WHERE r.state = 'SUSPENSE';
        """;

    public const string Down = """
        DELETE FROM bil.ledger_rule WHERE rule_id IN ('BLR-SUSPENSE-XFER-OUT', 'BLR-SUSPENSE-XFER-IN', 'BLR-SUSPENSE-REFUND');
        DELETE FROM bil.ledger_account WHERE account_code = 'LA-29';
        CREATE OR REPLACE FUNCTION bil.freeze_receipt() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.amount, NEW.currency, NEW.receipt_number, NEW.billing_account_id, NEW.value_date) IS DISTINCT FROM (OLD.amount, OLD.currency, OLD.receipt_number, OLD.billing_account_id, OLD.value_date) THEN
                RAISE EXCEPTION 'bil.receipt amounts and numbers are frozen once written' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;
        """;
}
