namespace CoreIns.Modules.Billing.Persistence;

/// <summary>
/// BO-BIL-07: the ledger rule of an allocation reversal and the database guard of reversal rows. The allocation table stays append-only;
/// a reversal row carries the negated amount and points at the allocation it undoes (unique: a row is reversed once).
/// </summary>
internal static class AllocationReversalSql
{
    public const string Up = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-ALLOCATION-REVERSED', 1, 'ALLOCATION_REVERSED', '*', 'DIRECT_BILL', '*', '*', 'LA-02', 'LA-11', 'AMOUNT', DATE '2000-01-01', NULL, 'BO-BIL-07 (allocation reversed: the mirror of BLR-ALLOCATED)');

        CREATE FUNCTION bil.guard_allocation_reversal() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            original bil.allocation%ROWTYPE;
        BEGIN
            IF NEW.reverses_allocation_id IS NULL THEN
                RETURN NEW;
            END IF;
            SELECT * INTO original FROM bil.allocation WHERE allocation_id = NEW.reverses_allocation_id;
            IF NOT FOUND OR original.reverses_allocation_id IS NOT NULL
               OR original.receipt_id <> NEW.receipt_id OR original.invoice_item_id <> NEW.invoice_item_id OR original.invoice_id <> NEW.invoice_id
               OR original.amount <> -NEW.amount OR original.currency <> NEW.currency THEN
                RAISE EXCEPTION 'a reversal must undo exactly one allocation of the same receipt and item (BO-BIL-07)' USING ERRCODE = 'BL001';
            END IF;
            RETURN NEW;
        END
        $fn$;
        CREATE TRIGGER tr_allocation_reversal_guard BEFORE INSERT ON bil.allocation FOR EACH ROW EXECUTE FUNCTION bil.guard_allocation_reversal();
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS tr_allocation_reversal_guard ON bil.allocation;
        DROP FUNCTION IF EXISTS bil.guard_allocation_reversal();
        DELETE FROM bil.ledger_rule WHERE rule_id = 'BLR-ALLOCATION-REVERSED';
        """;
}
