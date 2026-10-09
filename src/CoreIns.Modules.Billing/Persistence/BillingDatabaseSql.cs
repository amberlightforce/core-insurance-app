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
    /// account's IBAN, party, purpose and its control fields (cooling-off, change flag, superseded account, verification
    /// and VoP result: the app role cannot lift a hold) are frozen once written (SQLSTATE BL004); neither can be deleted (BL002). The
    /// sub-ledger itself keeps the append-only, balance and seal triggers above (D-ARC-34), which cover these entries too.
    /// </summary>
    public const string Disbursements = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-DISB-RELEASED-CLM', 1, 'DISBURSEMENT_RELEASED', '*', '*', '*', 'CLM_CLAIM_PAYMENT', 'LA-17', 'LA-13', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-211 (on release: claim payments clearing to disbursements in transit)'),
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
            IF (NEW.payee_account_id, NEW.legal_entity_id, NEW.party_id, NEW.purpose, NEW.iban_encrypted, NEW.iban_blind_index, NEW.iban_last4, NEW.valid_from, NEW.created_at,
                NEW.cooling_off_until, NEW.is_change, NEW.supersedes_id, NEW.verification_status, NEW.vop_result, NEW.vop_suggested_name, NEW.vop_checked_at)
               IS DISTINCT FROM
               (OLD.payee_account_id, OLD.legal_entity_id, OLD.party_id, OLD.purpose, OLD.iban_encrypted, OLD.iban_blind_index, OLD.iban_last4, OLD.valid_from, OLD.created_at,
                OLD.cooling_off_until, OLD.is_change, OLD.supersedes_id, OLD.verification_status, OLD.vop_result, OLD.vop_suggested_name, OLD.vop_checked_at) THEN
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

    /// <summary>
    /// SL3-BIL-CREDIT: the billing-ledger rules of credits (CREDIT_WRITTEN premium, surcharge and fee clearing → written
    /// unbilled; CREDIT_BILLED written unbilled → billed receivable; there is deliberately no tax or levy rule, so a tax or
    /// levy credit fails closed, D-SL3-05/06) and the database guards of credit notes:
    /// a credit item credits exactly one invoice item of the original invoice its credit note references, and Σ credit items
    /// per invoice item ≤ that item (SQLSTATE BL006); a credit application offsets exactly the invoice item its credit item
    /// credits, Σ per credit item ≤ the credit item and Σ (cash allocations + credit applications) per invoice item ≤ the item
    /// (BL001), under row locks; credit applications are append-only; the charge, invoice and item freeze triggers cover the new columns.
    /// </summary>
    public const string Credits = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-CREDIT-WRITTEN-PREMIUM', 1, 'CREDIT_WRITTEN', 'PREMIUM', 'DIRECT_BILL', '*', '*', 'LA-04', 'LA-01', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-073; PRD-06 §4.13 step 16'),
            ('BLR-CREDIT-WRITTEN-SURCHARGE', 1, 'CREDIT_WRITTEN', 'SURCHARGE', 'DIRECT_BILL', '*', '*', 'LA-04', 'LA-01', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-073 (written premium includes surcharges, D5)'),
            ('BLR-CREDIT-WRITTEN-FEE', 1, 'CREDIT_WRITTEN', 'FEE', 'DIRECT_BILL', '*', '*', 'LA-05', 'LA-01', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-073; PRD-06 §7.1.4 LA-05'),
            ('BLR-CREDIT-BILLED', 1, 'CREDIT_BILLED', '*', 'DIRECT_BILL', '*', '*', 'LA-01', 'LA-02', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-074, REQ-BIL-091; PRD-06 §4.13 step 17');

        CREATE OR REPLACE FUNCTION bil.freeze_charge() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.charge_id, NEW.set_id, NEW.set_size, NEW.set_index, NEW.policy_id, NEW.term_id, NEW.transaction_id, NEW.element_locator,
                NEW.coverage_code, NEW.charge_type, NEW.charge_category, NEW.delta_kind, NEW.amount, NEW.currency, NEW.valid_from, NEW.valid_to,
                NEW.booking_date, NEW.correlation_key, NEW.tax_treatment_ref, NEW.source_event_id,
                NEW.transaction_kind, NEW.cancellation_source, NEW.treatment_rule_id, NEW.treatment_rule_version)
               IS DISTINCT FROM
               (OLD.charge_id, OLD.set_id, OLD.set_size, OLD.set_index, OLD.policy_id, OLD.term_id, OLD.transaction_id, OLD.element_locator,
                OLD.coverage_code, OLD.charge_type, OLD.charge_category, OLD.delta_kind, OLD.amount, OLD.currency, OLD.valid_from, OLD.valid_to,
                OLD.booking_date, OLD.correlation_key, OLD.tax_treatment_ref, OLD.source_event_id,
                OLD.transaction_kind, OLD.cancellation_source, OLD.treatment_rule_id, OLD.treatment_rule_version) THEN
                RAISE EXCEPTION 'charge % is frozen as received (REQ-BIL-002)', OLD.charge_id USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE OR REPLACE FUNCTION bil.freeze_invoice() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.total, NEW.currency, NEW.invoice_number, NEW.billing_account_id, NEW.transaction_id, NEW.kind, NEW.original_invoice_id)
               IS DISTINCT FROM (OLD.total, OLD.currency, OLD.invoice_number, OLD.billing_account_id, OLD.transaction_id, OLD.kind, OLD.original_invoice_id) THEN
                RAISE EXCEPTION 'bil.invoice amounts and numbers are frozen once written' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE OR REPLACE FUNCTION bil.freeze_invoice_item() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.amount, NEW.currency, NEW.charge_id, NEW.invoice_id, NEW.credits_item_id, NEW.transaction_kind, NEW.cancellation_source, NEW.treatment_rule_id)
               IS DISTINCT FROM (OLD.amount, OLD.currency, OLD.charge_id, OLD.invoice_id, OLD.credits_item_id, OLD.transaction_kind, OLD.cancellation_source, OLD.treatment_rule_id) THEN
                RAISE EXCEPTION 'bil.invoice_item amounts and numbers are frozen once written' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE FUNCTION bil.guard_credit_item() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            original_amount numeric;
            original_invoice uuid;
            original_credits uuid;
            used numeric;
            note_kind text;
            note_original uuid;
            original_kind text;
        BEGIN
            IF NEW.credits_item_id IS NULL THEN
                RETURN NEW;
            END IF;
            SELECT amount, invoice_id, credits_item_id INTO original_amount, original_invoice, original_credits
                FROM bil.invoice_item WHERE invoice_item_id = NEW.credits_item_id FOR UPDATE;
            IF original_amount IS NULL OR original_credits IS NOT NULL THEN
                RAISE EXCEPTION 'credit item % credits no invoice item (REQ-BIL-091)', NEW.invoice_item_id USING ERRCODE = 'BL006';
            END IF;
            SELECT kind, original_invoice_id INTO note_kind, note_original FROM bil.invoice WHERE invoice_id = NEW.invoice_id;
            SELECT kind INTO original_kind FROM bil.invoice WHERE invoice_id = original_invoice;
            IF note_kind IS DISTINCT FROM 'CREDIT_NOTE' OR original_kind IS DISTINCT FROM 'INVOICE' OR note_original IS DISTINCT FROM original_invoice THEN
                RAISE EXCEPTION 'credit item % must belong to a credit note referencing the invoice of the item it credits (REQ-BIL-091)', NEW.invoice_item_id USING ERRCODE = 'BL006';
            END IF;
            SELECT coalesce(sum(amount), 0) INTO used FROM bil.invoice_item WHERE credits_item_id = NEW.credits_item_id;
            IF used + NEW.amount > original_amount THEN
                RAISE EXCEPTION 'credits of invoice item % would exceed it (REQ-BIL-073)', NEW.credits_item_id USING ERRCODE = 'BL006';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_invoice_item_credit_guard BEFORE INSERT ON bil.invoice_item FOR EACH ROW EXECUTE FUNCTION bil.guard_credit_item();

        CREATE OR REPLACE FUNCTION bil.guard_allocation() RETURNS trigger LANGUAGE plpgsql AS $fn$
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
            used := used + coalesce((SELECT sum(amount) FROM bil.credit_application WHERE target_invoice_item_id = NEW.invoice_item_id), 0);
            IF used + NEW.amount > limit_amount THEN
                RAISE EXCEPTION 'allocations of invoice item % would exceed it (REQ-BIL-130)', NEW.invoice_item_id USING ERRCODE = 'BL001';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE FUNCTION bil.guard_credit_application() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            credit_amount numeric;
            credited_item uuid;
            credit_invoice uuid;
            limit_amount numeric;
            used numeric;
        BEGIN
            SELECT amount, credits_item_id, invoice_id INTO credit_amount, credited_item, credit_invoice
                FROM bil.invoice_item WHERE invoice_item_id = NEW.credit_item_id FOR UPDATE;
            IF credit_amount IS NULL OR credited_item IS NULL OR credit_invoice <> NEW.credit_note_id THEN
                RAISE EXCEPTION 'credit application % is not from an item of credit note %', NEW.credit_application_id, NEW.credit_note_id USING ERRCODE = 'BL001';
            END IF;
            SELECT coalesce(sum(amount), 0) INTO used FROM bil.credit_application WHERE credit_item_id = NEW.credit_item_id;
            IF used + NEW.amount > credit_amount THEN
                RAISE EXCEPTION 'applications of credit item % would exceed it (REQ-BIL-073)', NEW.credit_item_id USING ERRCODE = 'BL001';
            END IF;
            IF NEW.target_kind = 'INVOICE_ITEM' THEN
                IF NEW.target_invoice_item_id IS DISTINCT FROM credited_item THEN
                    RAISE EXCEPTION 'a credit offsets only the invoice item it credits (REQ-BIL-073)' USING ERRCODE = 'BL001';
                END IF;
                SELECT amount INTO limit_amount FROM bil.invoice_item WHERE invoice_item_id = NEW.target_invoice_item_id FOR UPDATE;
                SELECT coalesce(sum(amount), 0) INTO used FROM bil.allocation WHERE invoice_item_id = NEW.target_invoice_item_id;
                used := used + coalesce((SELECT sum(amount) FROM bil.credit_application WHERE target_invoice_item_id = NEW.target_invoice_item_id), 0);
                IF used + NEW.amount > limit_amount THEN
                    RAISE EXCEPTION 'credit applications on invoice item % would exceed it (REQ-BIL-130)', NEW.target_invoice_item_id USING ERRCODE = 'BL001';
                END IF;
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_credit_application_guard BEFORE INSERT ON bil.credit_application FOR EACH ROW EXECUTE FUNCTION bil.guard_credit_application();
        CREATE TRIGGER tr_credit_application_append_only BEFORE UPDATE OR DELETE ON bil.credit_application FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_credit_application_no_truncate BEFORE TRUNCATE ON bil.credit_application FOR EACH STATEMENT EXECUTE FUNCTION bil.reject_change();
        """;

    public const string CreditsDown = """
        DROP TRIGGER IF EXISTS tr_invoice_item_credit_guard ON bil.invoice_item;
        DROP FUNCTION IF EXISTS bil.guard_credit_item() CASCADE;
        DROP FUNCTION IF EXISTS bil.guard_credit_application() CASCADE;
        DELETE FROM bil.ledger_rule WHERE rule_id IN ('BLR-CREDIT-WRITTEN-PREMIUM', 'BLR-CREDIT-WRITTEN-SURCHARGE', 'BLR-CREDIT-WRITTEN-FEE', 'BLR-CREDIT-BILLED');
        """;

    /// <summary>
    /// SL3-BIL-REFUND: the billing-ledger rules of refunds (REFUND_APPROVED: the credit leaves LA-02 for refunds payable LA-12 when
    /// the refund is approved, PRD-06 §4.13 step 18; DISBURSEMENT_RELEASED for source BIL_REFUND: LA-12 to disbursements in
    /// transit LA-13, step 20; the clearing rule is the existing wildcard one) and the database guards:
    /// <list type="bullet">
    /// <item>a credit is applied to a refund or netted only for an APPROVED refund (state APPROVED and approval APPROVED or
    /// NOT_REQUIRED) of the same account, and never above the refund's own credit or netting line, under row locks (BL001);
    /// netting only reaches an open item of a plain invoice of the same account and currency;</item>
    /// <item>a refund's content (amount, payee, payee account, lines, participants, hash) is frozen at creation, a Paid or
    /// Rejected refund is frozen entirely, and the approver can be neither a participant nor the person who changed the payee
    /// account (REQ-BIL-189) even if the application tried (BL004);</item>
    /// <item>the lines are append-only; refunds are never deleted.</item>
    /// </list>
    /// </summary>
    public const string Refunds = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-REFUND-APPROVED', 1, 'REFUND_APPROVED', '*', '*', '*', '*', 'LA-02', 'LA-12', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-181, REQ-BIL-190; PRD-06 §4.13 step 18 (credit balance to refunds payable)'),
            ('BLR-DISB-RELEASED-REFUND', 1, 'DISBURSEMENT_RELEASED', '*', '*', '*', 'BIL_REFUND', 'LA-12', 'LA-13', 'AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-211 (on release: refunds payable to disbursements in transit)');

        CREATE OR REPLACE FUNCTION bil.guard_credit_application() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            credit_amount numeric;
            credited_item uuid;
            credit_invoice uuid;
            limit_amount numeric;
            used numeric;
            line_amount numeric;
            refund_account uuid;
            refund_state text;
            refund_approval text;
            target_item_invoice uuid;
            target_inv_kind text;
            target_account uuid;
            target_currency text;
        BEGIN
            SELECT amount, credits_item_id, invoice_id INTO credit_amount, credited_item, credit_invoice
                FROM bil.invoice_item WHERE invoice_item_id = NEW.credit_item_id FOR UPDATE;
            IF credit_amount IS NULL OR credited_item IS NULL OR credit_invoice <> NEW.credit_note_id THEN
                RAISE EXCEPTION 'credit application % is not from an item of credit note %', NEW.credit_application_id, NEW.credit_note_id USING ERRCODE = 'BL001';
            END IF;
            SELECT coalesce(sum(amount), 0) INTO used FROM bil.credit_application WHERE credit_item_id = NEW.credit_item_id;
            IF used + NEW.amount > credit_amount THEN
                RAISE EXCEPTION 'applications of credit item % would exceed it (REQ-BIL-073)', NEW.credit_item_id USING ERRCODE = 'BL001';
            END IF;

            IF NEW.target_kind IN ('REFUND', 'NETTING') THEN
                SELECT billing_account_id, state, approval_state INTO refund_account, refund_state, refund_approval
                    FROM bil.refund WHERE refund_id = NEW.refund_id FOR UPDATE;
                IF refund_account IS DISTINCT FROM NEW.billing_account_id OR refund_state IS DISTINCT FROM 'APPROVED'
                   OR refund_approval NOT IN ('APPROVED', 'NOT_REQUIRED') THEN
                    RAISE EXCEPTION 'credit is applied to refund % only when it is approved (REQ-BIL-188)', NEW.refund_id USING ERRCODE = 'BL001';
                END IF;
            END IF;

            IF NEW.target_kind = 'INVOICE_ITEM' THEN
                IF NEW.target_invoice_item_id IS DISTINCT FROM credited_item THEN
                    RAISE EXCEPTION 'a credit offsets only the invoice item it credits (REQ-BIL-073)' USING ERRCODE = 'BL001';
                END IF;
                SELECT amount INTO limit_amount FROM bil.invoice_item WHERE invoice_item_id = NEW.target_invoice_item_id FOR UPDATE;
                SELECT coalesce(sum(amount), 0) INTO used FROM bil.allocation WHERE invoice_item_id = NEW.target_invoice_item_id;
                used := used + coalesce((SELECT sum(amount) FROM bil.credit_application WHERE target_invoice_item_id = NEW.target_invoice_item_id), 0);
                IF used + NEW.amount > limit_amount THEN
                    RAISE EXCEPTION 'credit applications on invoice item % would exceed it (REQ-BIL-130)', NEW.target_invoice_item_id USING ERRCODE = 'BL001';
                END IF;
            ELSIF NEW.target_kind = 'NETTING' THEN
                SELECT amount, invoice_id INTO limit_amount, target_item_invoice
                    FROM bil.invoice_item WHERE invoice_item_id = NEW.target_invoice_item_id FOR UPDATE;
                SELECT kind, billing_account_id, currency INTO target_inv_kind, target_account, target_currency
                    FROM bil.invoice WHERE invoice_id = NEW.target_invoice_id;
                IF target_item_invoice IS DISTINCT FROM NEW.target_invoice_id OR target_inv_kind IS DISTINCT FROM 'INVOICE'
                   OR target_account IS DISTINCT FROM NEW.billing_account_id OR target_currency IS DISTINCT FROM NEW.currency THEN
                    RAISE EXCEPTION 'netting reaches only an item of an invoice of the same account and currency (REQ-BIL-182)' USING ERRCODE = 'BL001';
                END IF;
                SELECT coalesce(sum(amount), 0) INTO used FROM bil.allocation WHERE invoice_item_id = NEW.target_invoice_item_id;
                used := used + coalesce((SELECT sum(amount) FROM bil.credit_application WHERE target_invoice_item_id = NEW.target_invoice_item_id), 0);
                IF used + NEW.amount > limit_amount THEN
                    RAISE EXCEPTION 'credit applications on invoice item % would exceed it (REQ-BIL-130)', NEW.target_invoice_item_id USING ERRCODE = 'BL001';
                END IF;
                SELECT amount INTO line_amount FROM bil.refund_netting
                    WHERE refund_id = NEW.refund_id AND credit_item_id = NEW.credit_item_id AND target_invoice_item_id = NEW.target_invoice_item_id;
                SELECT coalesce(sum(amount), 0) INTO used FROM bil.credit_application
                    WHERE refund_id = NEW.refund_id AND credit_item_id = NEW.credit_item_id AND target_invoice_item_id = NEW.target_invoice_item_id;
                IF line_amount IS NULL OR used + NEW.amount > line_amount THEN
                    RAISE EXCEPTION 'netting of refund % exceeds its netting line', NEW.refund_id USING ERRCODE = 'BL001';
                END IF;
            ELSIF NEW.target_kind = 'REFUND' THEN
                SELECT amount INTO line_amount FROM bil.refund_credit WHERE refund_id = NEW.refund_id AND credit_item_id = NEW.credit_item_id;
                SELECT coalesce(sum(amount), 0) INTO used FROM bil.credit_application
                    WHERE refund_id = NEW.refund_id AND credit_item_id = NEW.credit_item_id AND target_kind = 'REFUND';
                IF line_amount IS NULL OR used + NEW.amount > line_amount THEN
                    RAISE EXCEPTION 'refund % pays out more of credit item % than its line', NEW.refund_id, NEW.credit_item_id USING ERRCODE = 'BL001';
                END IF;
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE FUNCTION bil.freeze_refund() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.refund_id, NEW.legal_entity_id, NEW.jurisdiction, NEW.billing_account_id, NEW.amount, NEW.currency, NEW.payee_party_id, NEW.payee_account_id,
                NEW.payee_changed, NEW.payee_account_changed_by, NEW.payout_method, NEW.reason_code, NEW.selected_credit_notes, NEW.credit_set_key,
                NEW.resubmits_refund_id, NEW.participants, NEW.requested_by, NEW.approval_content_hash, NEW.proposed_at)
               IS DISTINCT FROM
               (OLD.refund_id, OLD.legal_entity_id, OLD.jurisdiction, OLD.billing_account_id, OLD.amount, OLD.currency, OLD.payee_party_id, OLD.payee_account_id,
                OLD.payee_changed, OLD.payee_account_changed_by, OLD.payout_method, OLD.reason_code, OLD.selected_credit_notes, OLD.credit_set_key,
                OLD.resubmits_refund_id, OLD.participants, OLD.requested_by, OLD.approval_content_hash, OLD.proposed_at) THEN
                RAISE LOG 'SECURITY: change of frozen refund % columns refused for role %', OLD.refund_id, current_user;
                RAISE EXCEPTION 'bil.refund amount, payee and lines are frozen once proposed; a rejected refund is resubmitted as a new one' USING ERRCODE = 'BL004';
            END IF;
            IF OLD.state IN ('PAID', 'REJECTED') AND NEW.state IS DISTINCT FROM OLD.state THEN
                RAISE EXCEPTION 'refund % is %, which is final', OLD.refund_id, OLD.state USING ERRCODE = 'BL004';
            END IF;
            -- The approval state only moves PENDING -> APPROVED/REJECTED, and only with the approval request that was made for it.
            -- NOT_REQUIRED exists from INSERT only (bil.guard_refund_insert); it is never reached by an update.
            IF NEW.approval_state IS DISTINCT FROM OLD.approval_state THEN
                IF OLD.approval_state <> 'PENDING' OR NEW.approval_state NOT IN ('APPROVED', 'REJECTED') OR OLD.approval_request_id IS NULL THEN
                    RAISE LOG 'SECURITY: refund % approval state change % -> % refused for role %', OLD.refund_id, OLD.approval_state, NEW.approval_state, current_user;
                    RAISE EXCEPTION 'refund approval state moves only from PENDING to APPROVED or REJECTED, through its approval request (REQ-BIL-188)' USING ERRCODE = 'BL004';
                END IF;
            END IF;
            IF NEW.state = 'APPROVED' AND OLD.state IS DISTINCT FROM 'APPROVED' THEN
                IF OLD.state <> 'PENDING_APPROVAL' OR NEW.approval_state <> 'APPROVED' THEN
                    RAISE LOG 'SECURITY: refund % moved to APPROVED from % with approval % refused for role %', OLD.refund_id, OLD.state, NEW.approval_state, current_user;
                    RAISE EXCEPTION 'a pending refund becomes APPROVED only with an approved approval (REQ-BIL-188)' USING ERRCODE = 'BL004';
                END IF;
            END IF;
            IF NEW.state = 'REJECTED' AND (OLD.state <> 'PENDING_APPROVAL' OR NEW.approval_state <> 'REJECTED') THEN
                RAISE EXCEPTION 'only a pending refund is rejected, with a rejected approval' USING ERRCODE = 'BL004';
            END IF;
            IF OLD.approval_request_id IS NOT NULL AND NEW.approval_request_id IS DISTINCT FROM OLD.approval_request_id THEN
                RAISE EXCEPTION 'the approval request of refund % is never replaced', OLD.refund_id USING ERRCODE = 'BL004';
            END IF;
            IF NEW.approval_state = 'APPROVED' AND (NEW.decided_by IS NULL OR NEW.decided_by = ANY (NEW.participants)
                                                  OR NEW.decided_by = NEW.payee_account_changed_by) THEN
                RAISE LOG 'SECURITY: refund % approved by a participant or the payee changer refused', OLD.refund_id;
                RAISE EXCEPTION 'a refund is approved by neither its requester, an editor nor the person who changed the payee account (REQ-BIL-189)' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_refund_frozen BEFORE UPDATE ON bil.refund FOR EACH ROW EXECUTE FUNCTION bil.freeze_refund();

        -- A refund starts pending (waiting for its approval request) or, only when approval is NOT_REQUIRED, already approved: no approval
        -- request, an unchanged payee, not a resubmission, and within the hard ceiling of the illustrative auto-approval limit (500.00;
        -- BIL's configured limit may be lower, never higher; the application checks the configured one first, D-SL3-08).
        CREATE FUNCTION bil.guard_refund_insert() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF NEW.approval_state = 'NOT_REQUIRED' THEN
                IF NEW.state <> 'APPROVED' OR NEW.approval_request_id IS NOT NULL OR NEW.payee_changed OR NEW.resubmits_refund_id IS NOT NULL
                   OR NEW.amount > 500.00 OR NEW.decided_by IS NOT NULL THEN
                    RAISE LOG 'SECURITY: refund % inserted as NOT_REQUIRED outside the rule refused for role %', NEW.refund_id, current_user;
                    RAISE EXCEPTION 'approval is not required only for an unchanged payee, within the auto limit, not after a rejection (REQ-BIL-188)' USING ERRCODE = 'BL004';
                END IF;
            ELSIF NEW.approval_state <> 'PENDING' OR NEW.state <> 'PENDING_APPROVAL' THEN
                RAISE EXCEPTION 'a refund starts PENDING_APPROVAL or approved by rule' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;

        CREATE TRIGGER tr_refund_insert_guard BEFORE INSERT ON bil.refund FOR EACH ROW EXECUTE FUNCTION bil.guard_refund_insert();
        CREATE TRIGGER tr_refund_append_only BEFORE DELETE ON bil.refund FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_refund_credit_append_only BEFORE UPDATE OR DELETE ON bil.refund_credit FOR EACH ROW EXECUTE FUNCTION bil.reject_change();
        CREATE TRIGGER tr_refund_netting_append_only BEFORE UPDATE OR DELETE ON bil.refund_netting FOR EACH ROW EXECUTE FUNCTION bil.reject_change();

        CREATE OR REPLACE FUNCTION bil.freeze_disbursement() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.disbursement_id, NEW.legal_entity_id, NEW.disbursement_number, NEW.source_module, NEW.source_type, NEW.source_id, NEW.claim_id,
                NEW.payee_party_id, NEW.payee_account_id, NEW.amount, NEW.currency, NEW.method, NEW.approval_evidence_ref, NEW.approval_content_hash, NEW.business_ref)
               IS DISTINCT FROM
               (OLD.disbursement_id, OLD.legal_entity_id, OLD.disbursement_number, OLD.source_module, OLD.source_type, OLD.source_id, OLD.claim_id,
                OLD.payee_party_id, OLD.payee_account_id, OLD.amount, OLD.currency, OLD.method, OLD.approval_evidence_ref, OLD.approval_content_hash, OLD.business_ref) THEN
                RAISE LOG 'SECURITY: change of frozen disbursement % columns refused for role %', OLD.disbursement_id, current_user;
                RAISE EXCEPTION 'bil.disbursement amounts, payee, source and number are frozen once written' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;
        """;

    public const string RefundsDown = """
        DELETE FROM bil.ledger_rule WHERE rule_id IN ('BLR-REFUND-APPROVED', 'BLR-DISB-RELEASED-REFUND');
        DROP FUNCTION IF EXISTS bil.freeze_refund() CASCADE;
        DROP FUNCTION IF EXISTS bil.guard_refund_insert() CASCADE;

        CREATE OR REPLACE FUNCTION bil.freeze_disbursement() RETURNS trigger LANGUAGE plpgsql AS $fn$
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

        CREATE OR REPLACE FUNCTION bil.guard_credit_application() RETURNS trigger LANGUAGE plpgsql AS $fn$
        DECLARE
            credit_amount numeric;
            credited_item uuid;
            credit_invoice uuid;
            limit_amount numeric;
            used numeric;
        BEGIN
            SELECT amount, credits_item_id, invoice_id INTO credit_amount, credited_item, credit_invoice
                FROM bil.invoice_item WHERE invoice_item_id = NEW.credit_item_id FOR UPDATE;
            IF credit_amount IS NULL OR credited_item IS NULL OR credit_invoice <> NEW.credit_note_id THEN
                RAISE EXCEPTION 'credit application % is not from an item of credit note %', NEW.credit_application_id, NEW.credit_note_id USING ERRCODE = 'BL001';
            END IF;
            SELECT coalesce(sum(amount), 0) INTO used FROM bil.credit_application WHERE credit_item_id = NEW.credit_item_id;
            IF used + NEW.amount > credit_amount THEN
                RAISE EXCEPTION 'applications of credit item % would exceed it (REQ-BIL-073)', NEW.credit_item_id USING ERRCODE = 'BL001';
            END IF;
            IF NEW.target_kind = 'INVOICE_ITEM' THEN
                IF NEW.target_invoice_item_id IS DISTINCT FROM credited_item THEN
                    RAISE EXCEPTION 'a credit offsets only the invoice item it credits (REQ-BIL-073)' USING ERRCODE = 'BL001';
                END IF;
                SELECT amount INTO limit_amount FROM bil.invoice_item WHERE invoice_item_id = NEW.target_invoice_item_id FOR UPDATE;
                SELECT coalesce(sum(amount), 0) INTO used FROM bil.allocation WHERE invoice_item_id = NEW.target_invoice_item_id;
                used := used + coalesce((SELECT sum(amount) FROM bil.credit_application WHERE target_invoice_item_id = NEW.target_invoice_item_id), 0);
                IF used + NEW.amount > limit_amount THEN
                    RAISE EXCEPTION 'credit applications on invoice item % would exceed it (REQ-BIL-130)', NEW.target_invoice_item_id USING ERRCODE = 'BL001';
                END IF;
            END IF;
            RETURN NEW;
        END
        $fn$;
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
