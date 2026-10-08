namespace CoreIns.Modules.Billing.Persistence;

/// <summary>
/// SQL the InitialBilling migration runs after creating the tables: the sub-ledger chart LA-01…LA-27 (PRD-06 §7.1.4,
/// names as the PRD gives them; † working translation, ‡ glossary-amended), the core billing-ledger rules of the slice
/// (REQ-BIL-286; jurisdiction-neutral, amount expression AMOUNT), and the database guards:
/// <list type="bullet">
/// <item>append-only sub-ledger and allocations: UPDATE, DELETE and TRUNCATE are refused for every role, including the
/// owner, and each attempt is written to the server log as a SECURITY line (REQ-BIL-281);</item>
/// <item>every entry balances per currency and has at least two lines, checked by a deferred constraint trigger at commit
/// (REQ-BIL-279, REQ-BIL-280);</item>
/// <item>Σ allocations ≤ receipt and Σ allocations ≤ invoice item, under row locks (REQ-BIL-130), SQLSTATE BL001;</item>
/// <item>a charge's received columns are frozen (REQ-BIL-002: charges are stored as received).</item>
/// </list>
/// </summary>
internal static class BillingDatabaseSql
{
    public const string Up = """
        INSERT INTO bil.ledger_account (account_code, name_en, name_el, account_type, normal_balance) VALUES
            ('LA-01', 'Written receivable — unbilled', 'Εγγεγραμμένες απαιτήσεις μη τιμολογημένες †', 'ASSET', 'DEBIT'),
            ('LA-02', 'Billed receivable — payer', 'Τιμολογημένες απαιτήσεις πληρωτή †', 'ASSET', 'DEBIT'),
            ('LA-03', 'Billed receivable — intermediary (agency bill)', 'Απαιτήσεις από διαμεσολαβητές †', 'ASSET', 'DEBIT'),
            ('LA-04', 'Premium written clearing', 'Ενδιάμεσος λογαριασμός εγγεγραμμένων ασφαλίστρων †', 'CLEARING', 'CREDIT'),
            ('LA-05', 'Fees and surcharges clearing', 'Ενδιάμεσος λογαριασμός εξόδων †', 'CLEARING', 'CREDIT'),
            ('LA-06', 'IPT payable', 'Φόρος ασφαλίστρων πληρωτέος †', 'LIABILITY', 'CREDIT'),
            ('LA-07', 'Levy payable', 'Εισφορές πληρωτέες †', 'LIABILITY', 'CREDIT'),
            ('LA-08', 'Levy expense clearing (insurer share)', 'Ενδιάμεσος λογαριασμός εξόδου εισφορών †', 'CLEARING', 'DEBIT'),
            ('LA-09', 'Cash in transit', 'Μετρητά υπό διακανονισμό †', 'ASSET', 'DEBIT'),
            ('LA-10', 'Cash at bank (per bank account)', 'Καταθέσεις όψεως †', 'ASSET', 'DEBIT'),
            ('LA-11', 'Suspense — unapplied cash', 'Μη αντιστοιχισμένες εισπράξεις †', 'LIABILITY', 'CREDIT'),
            ('LA-12', 'Refunds payable — customer credit', 'Πιστωτικά υπόλοιπα πελατών †', 'LIABILITY', 'CREDIT'),
            ('LA-13', 'Disbursements in transit', 'Πληρωμές υπό εκκαθάριση †', 'LIABILITY', 'CREDIT'),
            ('LA-14', 'Commission payable', 'Προμήθειες πληρωτέες', 'LIABILITY', 'CREDIT'),
            ('LA-15', 'Commission expense clearing', 'Ενδιάμεσος λογαριασμός εξόδου προμηθειών †', 'CLEARING', 'DEBIT'),
            ('LA-16', 'Write-off clearing', 'Ενδιάμεσος λογαριασμός διαγραφών †', 'CLEARING', 'DEBIT'),
            ('LA-17', 'Claim payments clearing', 'Ενδιάμεσος λογαριασμός πληρωμών ζημιών †', 'CLEARING', 'DEBIT'),
            ('LA-18', 'Acquirer and bank fees clearing', 'Ενδιάμεσος λογαριασμός τραπεζικών εξόδων †', 'CLEARING', 'DEBIT'),
            ('LA-19', 'Tolerance clearing', 'Ενδιάμεσος λογαριασμός ανοχής †', 'CLEARING', 'EITHER'),
            ('LA-20', 'Inter-account transfer clearing', 'Ενδιάμεσος λογαριασμός μεταφορών †', 'CLEARING', 'EITHER'),
            ('LA-21', 'Unclaimed funds', 'Αζήτητα ποσά †', 'LIABILITY', 'CREDIT'),
            ('LA-22', 'Reinsurance settlements clearing', 'Ενδιάμεσος λογαριασμός διακανονισμών αντασφάλισης †', 'CLEARING', 'EITHER'),
            ('LA-23', 'Reinsurer receivable', 'Απαιτήσεις από αντασφαλιστές †', 'ASSET', 'DEBIT'),
            ('LA-24', 'Friendly Settlement clearing', 'Ενδιάμεσος λογαριασμός φιλικού διακανονισμού ‡', 'CLEARING', 'EITHER'),
            ('LA-25', 'Redress clearing', 'Ενδιάμεσος λογαριασμός αποζημιώσεων παραπόνων ‡', 'CLEARING', 'DEBIT'),
            ('LA-26', 'Other tax payable (pack)', 'Λοιποί φόροι πληρωτέοι ‡', 'LIABILITY', 'CREDIT'),
            ('LA-27', 'IPT written not yet due', 'Φόρος ασφαλίστρων εγγεγραμμένος, μη απαιτητός ‡', 'LIABILITY', 'CREDIT');

        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-WRITTEN-PREMIUM', 1, 'WRITTEN', 'PREMIUM', 'DIRECT_BILL', '*', '*', 'LA-01', 'LA-04', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-066; PRD-06 §4.13 step 2'),
            ('BLR-WRITTEN-SURCHARGE', 1, 'WRITTEN', 'SURCHARGE', 'DIRECT_BILL', '*', '*', 'LA-01', 'LA-04', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-066 (written premium includes surcharges, D5)'),
            ('BLR-WRITTEN-FEE', 1, 'WRITTEN', 'FEE', 'DIRECT_BILL', '*', '*', 'LA-01', 'LA-05', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-066; PRD-06 §7.1.4 LA-05'),
            ('BLR-WRITTEN-TAX-DUE', 1, 'WRITTEN', 'TAX', 'DIRECT_BILL', '*', 'IPT_LIABILITY_DUE', 'LA-01', 'LA-27', 'AMOUNT', DATE '2000-01-01', NULL, 'PRD-06 §4.13 (liability point DUE: LA-27)'),
            ('BLR-WRITTEN-TAX-WRITTEN', 1, 'WRITTEN', 'TAX', 'DIRECT_BILL', '*', 'IPT_LIABILITY_WRITTEN', 'LA-01', 'LA-06', 'AMOUNT', DATE '2000-01-01', NULL, 'PRD-06 §4.13 (liability point WRITTEN: LA-06)'),
            ('BLR-WRITTEN-LEVY', 1, 'WRITTEN', 'LEVY', 'DIRECT_BILL', '*', '*', 'LA-01', 'LA-07', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-066; PRD-06 §4.13 step 2'),
            ('BLR-ACCRUED-LEVY', 1, 'ACCRUED', 'LEVY', 'DIRECT_BILL', '*', '*', 'LA-08', 'LA-07', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-066 (accrued not billed: levy expense clearing to levy payable)'),
            ('BLR-BILLED', 1, 'BILLED', '*', 'DIRECT_BILL', '*', '*', 'LA-02', 'LA-01', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-071; PRD-06 §4.13 step 3'),
            ('BLR-IPT-DUE', 1, 'IPT_DUE', 'TAX', 'DIRECT_BILL', '*', 'IPT_LIABILITY_DUE', 'LA-27', 'LA-06', 'AMOUNT', DATE '2000-01-01', NULL, 'PRD-06 §4.13 (LA-27 to LA-06 on each due date)'),
            ('BLR-RECEIVED', 1, 'RECEIVED', '*', 'DIRECT_BILL', '*', '*', 'LA-10', 'LA-11', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-126, REQ-BIL-131; PRD-06 §4.13 step 8 (via LA-11)'),
            ('BLR-ALLOCATED', 1, 'ALLOCATED', '*', 'DIRECT_BILL', '*', '*', 'LA-11', 'LA-02', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-129; PRD-06 §4.13 step 4');

        CREATE FUNCTION bil.reject_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            RAISE LOG 'SECURITY: % on append-only table bil.% refused for role % (REQ-BIL-281)', TG_OP, TG_TABLE_NAME, current_user;
            RAISE EXCEPTION 'bil.% is append-only: % is refused (REQ-BIL-281)', TG_TABLE_NAME, TG_OP USING ERRCODE = 'BL002';
        END
        $fn$;

        CREATE TRIGGER tr_ledger_entry_append_only BEFORE UPDATE OR DELETE ON bil.ledger_entry FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_ledger_entry_no_truncate BEFORE TRUNCATE ON bil.ledger_entry FOR EACH STATEMENT EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_ledger_line_append_only BEFORE UPDATE OR DELETE ON bil.ledger_line FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_ledger_line_no_truncate BEFORE TRUNCATE ON bil.ledger_line FOR EACH STATEMENT EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_allocation_append_only BEFORE UPDATE OR DELETE ON bil.allocation FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_allocation_no_truncate BEFORE TRUNCATE ON bil.allocation FOR EACH STATEMENT EXECUTE FUNCTION bil.reject_change();

        CREATE FUNCTION bil.check_entry_balanced() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            line_count integer;
            unbalanced text;
        BEGIN
            SELECT count(*) INTO line_count FROM bil.ledger_line WHERE entry_id = NEW.entry_id;
            IF line_count < 2 THEN
                RAISE EXCEPTION 'ledger entry % has % line(s); an entry needs at least two (REQ-BIL-279)', NEW.entry_id, line_count USING ERRCODE = 'BL003';
            END IF;
            SELECT currency INTO unbalanced FROM bil.ledger_line WHERE entry_id = NEW.entry_id
                GROUP BY currency HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0 LIMIT 1;
            IF unbalanced IS NOT NULL THEN
                RAISE EXCEPTION 'ledger entry % does not balance in % (REQ-BIL-280)', NEW.entry_id, unbalanced USING ERRCODE = 'BL003';
            END IF;
            IF NOT EXISTS (SELECT 1 FROM bil.ledger_entry WHERE entry_id = NEW.entry_id) THEN
                RAISE EXCEPTION 'ledger lines of entry % have no header (REQ-BIL-279)', NEW.entry_id USING ERRCODE = 'BL003';
            END IF;
            RETURN NULL;
        END
        $fn$;

        CREATE CONSTRAINT TRIGGER tr_ledger_entry_balanced AFTER INSERT ON bil.ledger_entry
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION bil.check_entry_balanced();
        CREATE CONSTRAINT TRIGGER tr_ledger_line_balanced AFTER INSERT ON bil.ledger_line
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION bil.check_entry_balanced();

        CREATE FUNCTION bil.guard_allocation() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            limit_amount numeric;
            used numeric;
        BEGIN
            SELECT amount INTO limit_amount FROM bil.receipt WHERE receipt_id = NEW.receipt_id FOR UPDATE;
            SELECT coalesce(sum(amount), 0) INTO used FROM bil.allocation WHERE receipt_id = NEW.receipt_id;
            IF used + NEW.amount > limit_amount THEN
                RAISE EXCEPTION 'allocations of receipt % would exceed it (REQ-BIL-130)', NEW.receipt_id USING ERRCODE = 'BL001';
            END IF;
            SELECT amount INTO limit_amount FROM bil.invoice_item WHERE invoice_item_id = NEW.invoice_item_id FOR UPDATE;
            SELECT coalesce(sum(amount), 0) INTO used FROM bil.allocation WHERE invoice_item_id = NEW.invoice_item_id;
            IF used + NEW.amount > limit_amount THEN
                RAISE EXCEPTION 'allocations of invoice item % would exceed it (REQ-BIL-130)', NEW.invoice_item_id USING ERRCODE = 'BL001';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_allocation_guard BEFORE INSERT ON bil.allocation FOR EACH ROW EXECUTE FUNCTION bil.guard_allocation();

        CREATE FUNCTION bil.freeze_charge() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.charge_id, NEW.set_id, NEW.set_size, NEW.set_index, NEW.policy_id, NEW.term_id, NEW.transaction_id, NEW.element_locator,
                NEW.coverage_code, NEW.charge_type, NEW.charge_category, NEW.delta_kind, NEW.amount, NEW.currency, NEW.valid_from, NEW.valid_to,
                NEW.booking_date, NEW.correlation_key, NEW.tax_treatment_ref, NEW.source_event_id)
               IS DISTINCT FROM
               (OLD.charge_id, OLD.set_id, OLD.set_size, OLD.set_index, OLD.policy_id, OLD.term_id, OLD.transaction_id, OLD.element_locator,
                OLD.coverage_code, OLD.charge_type, OLD.charge_category, OLD.delta_kind, OLD.amount, OLD.currency, OLD.valid_from, OLD.valid_to,
                OLD.booking_date, OLD.correlation_key, OLD.tax_treatment_ref, OLD.source_event_id) THEN
                RAISE EXCEPTION 'charge % is frozen as received (REQ-BIL-002)', OLD.charge_id USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_charge_frozen BEFORE UPDATE ON bil.charge FOR EACH ROW EXECUTE FUNCTION bil.freeze_charge();
        CREATE TRIGGER tr_charge_append_only BEFORE DELETE ON bil.charge FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        """;

    public const string Down = """
        DROP FUNCTION IF EXISTS bil.reject_change() CASCADE;
        DROP FUNCTION IF EXISTS bil.check_entry_balanced() CASCADE;
        DROP FUNCTION IF EXISTS bil.guard_allocation() CASCADE;
        DROP FUNCTION IF EXISTS bil.freeze_charge() CASCADE;
        """;

    /// <summary>
    /// D-ARC-34: each entry is sealed at its creating transaction. <c>ledger_entry.created_txid</c> records the top-level
    /// transaction id (txid_current() is the same inside EF's savepoints); a BEFORE INSERT trigger on <c>ledger_line</c>
    /// refuses a line whose header was not inserted by the current transaction (SQLSTATE BL005), so a balanced pair
    /// appended to an existing entry later is refused even though it would pass the balance check. The same migration
    /// freezes the money and number columns of invoices, items and receipts (SQLSTATE BL004): states move, amounts never.
    /// </summary>
    public const string Seal = """
        ALTER TABLE bil.ledger_entry ADD COLUMN created_txid bigint NOT NULL DEFAULT txid_current();

        CREATE FUNCTION bil.seal_ledger_line() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            header_txid bigint;
        BEGIN
            SELECT created_txid INTO header_txid FROM bil.ledger_entry WHERE entry_id = NEW.entry_id;
            IF header_txid IS NULL THEN
                RAISE EXCEPTION 'ledger line for entry % has no header (REQ-BIL-279)', NEW.entry_id USING ERRCODE = 'BL005';
            END IF;
            IF header_txid <> txid_current() THEN
                RAISE LOG 'SECURITY: line appended to sealed ledger entry % by role % (D-ARC-34)', NEW.entry_id, current_user;
                RAISE EXCEPTION 'ledger entry % is sealed: lines are written only by the transaction that created it (D-ARC-34)', NEW.entry_id
                    USING ERRCODE = 'BL005';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_ledger_line_sealed BEFORE INSERT ON bil.ledger_line FOR EACH ROW EXECUTE FUNCTION bil.seal_ledger_line();

        CREATE FUNCTION bil.freeze_invoice() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.total, NEW.currency, NEW.invoice_number, NEW.billing_account_id, NEW.transaction_id, NEW.kind) IS DISTINCT FROM (OLD.total, OLD.currency, OLD.invoice_number, OLD.billing_account_id, OLD.transaction_id, OLD.kind) THEN
                RAISE EXCEPTION 'bil.invoice amounts and numbers are frozen once written' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_invoice_frozen BEFORE UPDATE ON bil.invoice FOR EACH ROW EXECUTE FUNCTION bil.freeze_invoice();

        CREATE FUNCTION bil.freeze_invoice_item() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.amount, NEW.currency, NEW.charge_id, NEW.invoice_id) IS DISTINCT FROM (OLD.amount, OLD.currency, OLD.charge_id, OLD.invoice_id) THEN
                RAISE EXCEPTION 'bil.invoice_item amounts and numbers are frozen once written' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_invoice_item_frozen BEFORE UPDATE ON bil.invoice_item FOR EACH ROW EXECUTE FUNCTION bil.freeze_invoice_item();

        CREATE FUNCTION bil.freeze_receipt() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.amount, NEW.currency, NEW.receipt_number, NEW.billing_account_id, NEW.value_date) IS DISTINCT FROM (OLD.amount, OLD.currency, OLD.receipt_number, OLD.billing_account_id, OLD.value_date) THEN
                RAISE EXCEPTION 'bil.receipt amounts and numbers are frozen once written' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_receipt_frozen BEFORE UPDATE ON bil.receipt FOR EACH ROW EXECUTE FUNCTION bil.freeze_receipt();

        CREATE TRIGGER tr_invoice_append_only BEFORE DELETE ON bil.invoice FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_invoice_item_append_only BEFORE DELETE ON bil.invoice_item FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_receipt_append_only BEFORE DELETE ON bil.receipt FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        """;

    /// <summary>
    /// SL2-BIL-DISB: the billing-ledger rules of disbursement facts (REQ-BIL-211; key event type × charge category × bill
    /// mode × jurisdiction × qualifier, the qualifier naming the source type of the source register, REQ-BIL-354) and the
    /// database guards of the new documents: a disbursement's money, payee, source and number columns and a payee
    /// account's IBAN, party and purpose are frozen once written (SQLSTATE BL004); neither can be deleted (BL002). The
    /// sub-ledger itself keeps the append-only, balance and seal triggers above (D-ARC-34), which cover these entries too.
    /// </summary>
    public const string Disbursements = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-DISB-RELEASED-CLM', 1, 'DISBURSEMENT_RELEASED', '*', '*', '*', 'CLM_PAYMENT', 'LA-17', 'LA-13', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-211 (on release: claim payments clearing to disbursements in transit)'),
            ('BLR-DISB-CLEARED', 1, 'DISBURSEMENT_CLEARED', '*', '*', '*', '*', 'LA-13', 'LA-10', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-211 (on clearing: disbursements in transit to cash at bank)');

        CREATE FUNCTION bil.freeze_disbursement() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.disbursement_id, NEW.legal_entity_id, NEW.disbursement_number, NEW.source_module, NEW.source_type, NEW.source_id, NEW.claim_id,
                NEW.payee_party_id, NEW.payee_account_id, NEW.amount, NEW.currency, NEW.method, NEW.approval_evidence_ref, NEW.approval_content_hash)
               IS DISTINCT FROM
               (OLD.disbursement_id, OLD.legal_entity_id, OLD.disbursement_number, OLD.source_module, OLD.source_type, OLD.source_id, OLD.claim_id,
                OLD.payee_party_id, OLD.payee_account_id, OLD.amount, OLD.currency, OLD.method, OLD.approval_evidence_ref, OLD.approval_content_hash) THEN
                RAISE LOG 'SECURITY: change of frozen disbursement % columns refused for role %', OLD.disbursement_id, current_user;
                RAISE EXCEPTION 'bil.disbursement amounts, payee, source and number are frozen once written' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_disbursement_frozen BEFORE UPDATE ON bil.disbursement FOR EACH ROW EXECUTE FUNCTION bil.freeze_disbursement();
        CREATE TRIGGER tr_disbursement_append_only BEFORE DELETE ON bil.disbursement FOR EACH ROW EXECUTE FUNCTION bil.reject_change();

        CREATE FUNCTION bil.freeze_payee_account() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.payee_account_id, NEW.legal_entity_id, NEW.party_id, NEW.purpose, NEW.iban_encrypted, NEW.iban_blind_index, NEW.iban_last4, NEW.valid_from, NEW.created_at)
               IS DISTINCT FROM
               (OLD.payee_account_id, OLD.legal_entity_id, OLD.party_id, OLD.purpose, OLD.iban_encrypted, OLD.iban_blind_index, OLD.iban_last4, OLD.valid_from, OLD.created_at) THEN
                RAISE LOG 'SECURITY: change of frozen payee account % columns refused for role %', OLD.payee_account_id, current_user;
                RAISE EXCEPTION 'bil.payee_account bank details are never overwritten: a change supersedes the account (REQ-BIL-343)' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_payee_account_frozen BEFORE UPDATE ON bil.payee_account FOR EACH ROW EXECUTE FUNCTION bil.freeze_payee_account();
        CREATE TRIGGER tr_payee_account_append_only BEFORE DELETE ON bil.payee_account FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        """;

    public const string DisbursementsDown = """
        DELETE FROM bil.ledger_rule WHERE rule_id IN ('BLR-DISB-RELEASED-CLM', 'BLR-DISB-CLEARED');
        DROP FUNCTION IF EXISTS bil.freeze_disbursement() CASCADE;
        DROP FUNCTION IF EXISTS bil.freeze_payee_account() CASCADE;
        """;

    public const string SealDown = """
        DROP TRIGGER IF EXISTS tr_invoice_append_only ON bil.invoice;
        DROP TRIGGER IF EXISTS tr_invoice_item_append_only ON bil.invoice_item;
        DROP TRIGGER IF EXISTS tr_receipt_append_only ON bil.receipt;
        DROP FUNCTION IF EXISTS bil.freeze_invoice() CASCADE;
        DROP FUNCTION IF EXISTS bil.freeze_invoice_item() CASCADE;
        DROP FUNCTION IF EXISTS bil.freeze_receipt() CASCADE;
        DROP FUNCTION IF EXISTS bil.seal_ledger_line() CASCADE;
        ALTER TABLE bil.ledger_entry DROP COLUMN IF EXISTS created_txid;
        """;
}
