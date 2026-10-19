namespace CoreIns.Modules.Billing.Persistence;

/// <summary>SL4-BIL-DISBOPS billing-ledger rules (data): the DISBURSEMENT_REVERSED legs FIN v4 maps (D-SL4-13, REQ-BIL-208, -210).</summary>
internal static class DisbursementOpsDatabaseSql
{
    // The qualifier names the source type for the in-transit leg (disbursements in transit LA-13 back to the source payable)
    // and CASH for the leg that undoes a statement debit (cash at bank LA-10 back to LA-13).
    public const string Rules = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier, debit_account, credit_account, amount_expression, valid_from, valid_to, source)
        VALUES ('BLR-DISB-REV-CLM',1,'DISBURSEMENT_REVERSED','*','*','*','CLM_CLAIM_PAYMENT','LA-13','LA-17','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-208, -210; SL4-BIL-DISBOPS (reversal of a released claim payment: in transit back to claim payments clearing)'),
               ('BLR-DISB-REV-REFUND',1,'DISBURSEMENT_REVERSED','*','*','*','BIL_REFUND','LA-13','LA-12','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-208, -210; SL4-BIL-DISBOPS (reversal of a released refund: in transit back to refunds payable)'),
               ('BLR-DISB-REV-FS',1,'DISBURSEMENT_REVERSED','*','*','*','FS_CLEARING','LA-13','LA-24','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-208, -357; SL4-BIL-DISBOPS (reversal of a released FS net: in transit back to Friendly Settlement clearing)'),
               ('BLR-DISB-REV-CASH',1,'DISBURSEMENT_REVERSED','*','*','*','CASH','LA-10','LA-13','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-210; SL4-BIL-DISBOPS (return after the statement debit: cash at bank back to in transit)')
        ON CONFLICT DO NOTHING;
        """;

    public const string RulesDown = """
        DELETE FROM bil.ledger_rule WHERE rule_id IN ('BLR-DISB-REV-CLM','BLR-DISB-REV-REFUND','BLR-DISB-REV-FS','BLR-DISB-REV-CASH');
        """;
}
