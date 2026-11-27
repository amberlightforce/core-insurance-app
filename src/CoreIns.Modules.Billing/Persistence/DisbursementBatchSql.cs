namespace CoreIns.Modules.Billing.Persistence;

/// <summary>
/// BO-BIL-10 billing-ledger rules (data): the bank release of the four non-claim sources (REQ-BIL-211) and its reversal after a stop or
/// void (REQ-BIL-208, -210). The qualifier names the source type. Release moves the source payable to disbursements in transit LA-13
/// (the clearing then moves LA-13 to cash through the shared DISBURSEMENT_CLEARED rule); the reversal gives the payable back.
/// FIN maps the payable accounts in rule set v6 (gr-test.finance.v6).
/// </summary>
internal static class DisbursementBatchSql
{
    public const string Rules = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier, debit_account, credit_account, amount_expression, valid_from, valid_to, source)
        VALUES ('BLR-DISB-RELEASED-COMMISSION',1,'DISBURSEMENT_RELEASED','*','*','*','BIL_COMMISSION','LA-14','LA-13','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-211, BO-BIL-10 (on release: commission payable to disbursements in transit)'),
               ('BLR-DISB-RELEASED-LEVY',1,'DISBURSEMENT_RELEASED','*','*','*','BIL_LEVY','LA-07','LA-13','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-211, BO-BIL-10 (on release: levy payable to disbursements in transit)'),
               ('BLR-DISB-RELEASED-TAX',1,'DISBURSEMENT_RELEASED','*','*','*','TAX_REMITTANCE','LA-06','LA-13','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-211, BO-BIL-10 (on release: IPT payable to disbursements in transit)'),
               ('BLR-DISB-RELEASED-OTHER',1,'DISBURSEMENT_RELEASED','*','*','*','BIL_OTHER','LA-20','LA-13','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-211, BO-BIL-10 (on release: other payments clearing to disbursements in transit)'),
               ('BLR-DISB-REV-COMMISSION',1,'DISBURSEMENT_REVERSED','*','*','*','BIL_COMMISSION','LA-13','LA-14','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-208, -210, BO-BIL-10 (reversal of a released commission payment: in transit back to commission payable)'),
               ('BLR-DISB-REV-LEVY',1,'DISBURSEMENT_REVERSED','*','*','*','BIL_LEVY','LA-13','LA-07','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-208, -210, BO-BIL-10 (reversal of a released levy payment: in transit back to levy payable)'),
               ('BLR-DISB-REV-TAX',1,'DISBURSEMENT_REVERSED','*','*','*','TAX_REMITTANCE','LA-13','LA-06','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-208, -210, BO-BIL-10 (reversal of a released tax remittance: in transit back to IPT payable)'),
               ('BLR-DISB-REV-OTHER',1,'DISBURSEMENT_REVERSED','*','*','*','BIL_OTHER','LA-13','LA-20','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-208, -210, BO-BIL-10 (reversal of a released other payment: in transit back to other payments clearing)')
        ON CONFLICT DO NOTHING;
        """;

    public const string RulesDown = """
        DELETE FROM bil.ledger_rule WHERE rule_id IN ('BLR-DISB-RELEASED-COMMISSION','BLR-DISB-RELEASED-LEVY','BLR-DISB-RELEASED-TAX','BLR-DISB-RELEASED-OTHER',
            'BLR-DISB-REV-COMMISSION','BLR-DISB-REV-LEVY','BLR-DISB-REV-TAX','BLR-DISB-REV-OTHER');
        """;
}
