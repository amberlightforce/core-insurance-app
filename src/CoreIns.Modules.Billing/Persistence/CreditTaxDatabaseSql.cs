namespace CoreIns.Modules.Billing.Persistence;

/// <summary>GAP-SL3-BIL-VOID: the billing-ledger rule of an IPT credit (distance withdrawal / void, REDUCE_PRO_RATA).</summary>
internal static class CreditTaxDatabaseSql
{
    /// <summary>
    /// CREDIT_WRITTEN of a tax line: IPT payable LA-06 debited against the not-yet-billed receivable LA-01 (PRD-06 section 4.13). Under
    /// liability point DUE the original posted LA-01 to LA-27 on writing and LA-27 to LA-06 on billing; the reversal nets both
    /// steps in one entry, because a credit note is billed together with its write-off. CREDIT_BILLED (LA-01 to LA-02) is the existing wildcard rule.
    /// </summary>
    public const string Rules = """
        INSERT INTO bil.ledger_rule (rule_id, version, event_type, charge_category, bill_mode, jurisdiction, qualifier,
            debit_account, credit_account, amount_expression, valid_from, valid_to, source) VALUES
            ('BLR-CREDIT-WRITTEN-TAX', 1, 'CREDIT_WRITTEN', 'TAX', 'DIRECT_BILL', '*', '*', 'LA-06', 'LA-01', 'AMOUNT', DATE '2000-01-01', NULL, 'GAP-SL3-BIL-VOID; REQ-BIL-073; PRD-06 section 4.13 (IPT credit reverses the payable)');
        """;

    public const string RulesDown = "DELETE FROM bil.ledger_rule WHERE rule_id = 'BLR-CREDIT-WRITTEN-TAX';";
}
