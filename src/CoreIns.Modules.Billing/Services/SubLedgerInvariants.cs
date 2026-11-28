using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// The sub-ledger invariants INV-01..INV-15 (BO-BIL-11, REQ-BIL-296/321), run on demand over the billing tables of the legal entity as of an
/// accounting date, optionally for one billing account. Each is a read-only query that returns the key of every broken row; a clean
/// ledger returns none. INV-01..04 and 15 are structural (balance, shape, reversals, ownership), INV-05..10 tie cash, allocations, invoices
/// and suspense together, INV-11..12 tie disbursements to their ledger entries, INV-13..14 tie the bank statements to receipts and balances.
/// Checks that have no billing account (disbursements, statements) ignore the account filter.
/// TODO(hardening): the numbering and wording of INV-01..15 follow the build-out, not the PRD-06 section 14 text (not in the repository);
/// align them with it, add the nightly schedule and the severity-high incident of REQ-BIL-285, and the cancellation-sourced IPT check (INV-05 of the PRD).
/// </summary>
internal sealed class SubLedgerInvariants(BillingDbContext db, LedgerWriter ledger)
{
    private const string EntryScope = "e.legal_entity_id = @le AND e.accounting_date <= @asof AND (@acct::uuid IS NULL OR e.billing_account_id = @acct::uuid)";

    private static readonly (string Id, string Name, string Sql)[] Checks =
    [
        ("INV-01", "Every ledger entry balances to zero per currency",
            $"""
            SELECT e.entry_id::text FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
            WHERE {EntryScope}
            GROUP BY e.entry_id, l.currency HAVING sum(CASE l.side WHEN 'DEBIT' THEN l.amount ELSE -l.amount END) <> 0
            """),
        ("INV-02", "Every ledger entry has at least two lines",
            $"""
            SELECT e.entry_id::text FROM bil.ledger_entry e LEFT JOIN bil.ledger_line l ON l.entry_id = e.entry_id
            WHERE {EntryScope} GROUP BY e.entry_id HAVING count(l.line_id) < 2
            """),
        ("INV-03", "Every ledger line has a positive amount",
            $"""
            SELECT l.line_id::text FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
            WHERE {EntryScope} AND l.amount <= 0
            """),
        ("INV-04", "A reversal entry is the exact mirror of the entry it reverses",
            $"""
            SELECT e.entry_id::text FROM bil.ledger_entry e
            WHERE {EntryScope} AND e.reverses_entry_id IS NOT NULL
              AND (SELECT array_agg(x ORDER BY x) FROM (SELECT l.account_code || l.side || l.amount::text AS x FROM bil.ledger_line l WHERE l.entry_id = e.entry_id) a)
                  IS DISTINCT FROM
                  (SELECT array_agg(x ORDER BY x) FROM (SELECT l.account_code || (CASE l.side WHEN 'DEBIT' THEN 'CREDIT' ELSE 'DEBIT' END) || l.amount::text AS x
                    FROM bil.ledger_line l WHERE l.entry_id = e.reverses_entry_id) b)
            """),
        ("INV-05", "Cash allocated from a receipt never exceeds the receipt",
            """
            SELECT r.receipt_id::text FROM bil.receipt r
            WHERE r.legal_entity_id = @le AND r.accounting_date <= @asof AND (@acct::uuid IS NULL OR r.billing_account_id = @acct::uuid)
              AND (SELECT coalesce(sum(a.amount), 0) FROM bil.allocation a WHERE a.receipt_id = r.receipt_id)
                + (SELECT coalesce(sum(ra.amount), 0) FROM bil.receivable_allocation ra WHERE ra.receipt_id = r.receipt_id) > r.amount
            """),
        ("INV-06", "Cash allocated to an invoice item never exceeds the item",
            """
            SELECT i.invoice_item_id::text FROM bil.invoice_item i JOIN bil.invoice v ON v.invoice_id = i.invoice_id
            WHERE i.legal_entity_id = @le AND v.issue_date <= @asof AND (@acct::uuid IS NULL OR v.billing_account_id = @acct::uuid)
              AND i.amount > 0 AND (SELECT coalesce(sum(a.amount), 0) FROM bil.allocation a WHERE a.invoice_item_id = i.invoice_item_id) > i.amount
            """),
        ("INV-07", "An invoice total equals the sum of its items",
            """
            SELECT v.invoice_id::text FROM bil.invoice v
            WHERE v.legal_entity_id = @le AND v.issue_date <= @asof AND (@acct::uuid IS NULL OR v.billing_account_id = @acct::uuid)
              AND v.total <> (SELECT coalesce(sum(i.amount), 0) FROM bil.invoice_item i WHERE i.invoice_id = v.invoice_id)
            """),
        ("INV-08", "Unapplied cash (LA-11) of a billing account is never negative",
            $"""
            SELECT l.billing_account_id::text FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
            WHERE {EntryScope} AND l.account_code = 'LA-11' AND l.billing_account_id IS NOT NULL
            GROUP BY l.billing_account_id HAVING sum(CASE l.side WHEN 'CREDIT' THEN l.amount ELSE -l.amount END) < 0
            """),
        ("INV-09", "Every receipt in suspense has one open suspense item, and every open item a receipt in suspense",
            """
            SELECT r.receipt_id::text FROM bil.receipt r
            WHERE r.legal_entity_id = @le AND r.accounting_date <= @asof AND (@acct::uuid IS NULL OR r.billing_account_id = @acct::uuid) AND r.state = 'SUSPENSE'
              AND (SELECT count(*) FROM bil.suspense_item s WHERE s.receipt_id = r.receipt_id AND s.state IN ('OPEN', 'REFUND_PENDING')) <> 1
            UNION ALL
            SELECT s.suspense_item_id::text FROM bil.suspense_item s JOIN bil.receipt r ON r.receipt_id = s.receipt_id
            WHERE s.legal_entity_id = @le AND r.accounting_date <= @asof AND (@acct::uuid IS NULL OR s.billing_account_id = @acct::uuid)
              AND s.state IN ('OPEN', 'REFUND_PENDING') AND r.state <> 'SUSPENSE'
            """),
        ("INV-10", "A suspense item carries the amount and currency of its receipt",
            """
            SELECT s.suspense_item_id::text FROM bil.suspense_item s JOIN bil.receipt r ON r.receipt_id = s.receipt_id
            WHERE s.legal_entity_id = @le AND r.accounting_date <= @asof AND (@acct::uuid IS NULL OR s.billing_account_id = @acct::uuid)
              AND (s.amount <> r.amount OR s.currency <> r.currency)
            """),
        ("INV-11", "A disbursement released to the bank has its release entry",
            """
            SELECT d.disbursement_id::text FROM bil.disbursement d
            WHERE d.legal_entity_id = @le AND d.state IN ('RELEASED', 'ISSUED', 'CLEARED') AND (d.released_at IS NULL OR d.released_at < (@asof::date + 1))
              AND NOT EXISTS (SELECT 1 FROM bil.ledger_entry e WHERE e.disbursement_id = d.disbursement_id AND e.entry_type IN ('DISBURSEMENT_RELEASED', 'FS_CLEARING_RELEASED'))
            """),
        ("INV-12", "A cleared disbursement has its clearing entry",
            """
            SELECT d.disbursement_id::text FROM bil.disbursement d
            WHERE d.legal_entity_id = @le AND d.state = 'CLEARED' AND (d.cleared_at IS NULL OR d.cleared_at < (@asof::date + 1))
              AND NOT EXISTS (SELECT 1 FROM bil.ledger_entry e WHERE e.disbursement_id = d.disbursement_id AND e.entry_type IN ('DISBURSEMENT_CLEARED', 'FS_CLEARING_CLEARED'))
            """),
        ("INV-13", "A bank statement credit matched to a receipt has the receipt's amount",
            """
            SELECT l.line_id::text FROM bil.bank_statement_line l JOIN bil.receipt r ON r.receipt_id = l.receipt_id
            WHERE l.legal_entity_id = @le AND l.booking_date <= @asof AND (@acct::uuid IS NULL OR r.billing_account_id = @acct::uuid)
              AND l.status = 'MATCHED' AND (l.amount <> r.amount OR l.currency <> r.currency)
            """),
        ("INV-14", "A bank statement's closing balance equals its opening balance plus its lines",
            """
            SELECT s.statement_id::text FROM bil.bank_statement s
            WHERE s.legal_entity_id = @le AND s.to_date <= @asof AND s.opening_balance IS NOT NULL AND s.closing_balance IS NOT NULL
              AND s.closing_balance <> s.opening_balance
                + (SELECT coalesce(sum(CASE l.direction WHEN 'CREDIT' THEN l.amount ELSE -l.amount END), 0) FROM bil.bank_statement_line l WHERE l.statement_id = s.statement_id)
            """),
        ("INV-15", "Every ledger line belongs to the legal entity of its entry",
            $"""
            SELECT l.line_id::text FROM bil.ledger_entry e JOIN bil.ledger_line l ON l.entry_id = e.entry_id
            WHERE {EntryScope} AND l.legal_entity_id <> e.legal_entity_id
            """),
    ];

    public async Task<ReconciliationInvariantsResponse> RunAsync(BusinessDate? asOf, Guid? account, CancellationToken cancellationToken)
    {
        var date = asOf ?? ledger.Today;
        var legalEntity = ledger.LegalEntityId.Value;
        var results = new List<InvariantResult>(Checks.Length);
        foreach (var (id, name, sql) in Checks)
        {
            // Both queries bind the same three values; parameters are not shared between commands.
            var countSql = "SELECT count(*) AS \"Value\" FROM (" + sql + ") b(key)";
            var sampleSql = "SELECT key AS \"Value\" FROM (" + sql + ") b(key) ORDER BY key LIMIT 5";
            var count = await db.Database.SqlQueryRaw<long>(countSql, Parameters(legalEntity, date, account))
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            var samples = count == 0
                ? []
                : await db.Database.SqlQueryRaw<string>(sampleSql, Parameters(legalEntity, date, account))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            results.Add(new InvariantResult
            {
                InvariantId = id,
                Name = name,
                Status = count == 0 ? InvariantResult.StatusValue.Pass : InvariantResult.StatusValue.Fail,
                BreakCount = (int)Math.Min(count, int.MaxValue),
                Samples = samples,
            });
        }

        var total = results.Sum(r => (long)r.BreakCount);
        return new ReconciliationInvariantsResponse
        {
            Status = total == 0 ? ReconciliationInvariantsResponse.StatusValue.Pass : ReconciliationInvariantsResponse.StatusValue.Fail,
            AsOf = date,
            Results = results,
            Breaks = (int)Math.Min(total, int.MaxValue),
        };
    }

    private static object[] Parameters(Guid legalEntity, BusinessDate date, Guid? account) =>
    [
        new NpgsqlParameter("le", NpgsqlDbType.Uuid) { Value = legalEntity },
        new NpgsqlParameter("asof", NpgsqlDbType.Date) { Value = date.Value },
        new NpgsqlParameter("acct", NpgsqlDbType.Uuid) { Value = account is { } a ? a : DBNull.Value },
    ];
}
