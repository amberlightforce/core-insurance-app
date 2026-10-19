namespace CoreIns.Modules.Billing.Persistence;

/// <summary>SL4-BIL-FSOUT billing-ledger rules (data) and the receivable cash-leg correction.</summary>
internal static class FsClearingDatabaseSql
{
    public const string Rules = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier, debit_account, credit_account, amount_expression, valid_from, valid_to, source)
        VALUES ('BLR-FS-RELEASED',1,'FS_CLEARING_RELEASED','*','*','*','FS_CLEARING','LA-24','LA-13','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-357; SL4-BIL-FSOUT (on release: Friendly Settlement clearing to disbursements in transit)'),
               ('BLR-FS-CLEARED',1,'FS_CLEARING_CLEARED','*','*','*','FS_CLEARING','LA-13','LA-10','AMOUNT', DATE '2000-01-01', NULL, 'REQ-BIL-357; SL4-BIL-FSOUT (on clearing: disbursements in transit to cash at bank)')
        ON CONFLICT DO NOTHING;
        UPDATE bil.ledger_rule SET debit_account = 'LA-11'
        WHERE rule_id IN ('BLR-COLLECT-CLM','BLR-COLLECT-FS') AND debit_account = 'LA-10';
        """;

    public const string RulesDown = """
        UPDATE bil.ledger_rule SET debit_account = 'LA-10'
        WHERE rule_id IN ('BLR-COLLECT-CLM','BLR-COLLECT-FS') AND debit_account = 'LA-11';
        DELETE FROM bil.ledger_rule WHERE rule_id IN ('BLR-FS-RELEASED','BLR-FS-CLEARED');
        """;
}
