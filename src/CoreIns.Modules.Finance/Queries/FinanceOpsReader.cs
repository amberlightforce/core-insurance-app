using System.Globalization;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Finance.Queries;

/// <summary>One account row of the trial balance (functional currency; amounts as invariant decimal strings).</summary>
internal sealed record TrialBalanceRow(string Book, string Account, string NameEl, string NameEn, string Debit, string Credit, string Balance);

/// <summary><c>fin.Balance.trialBalance</c> result: balances of one calendar-month period.</summary>
internal sealed record TrialBalanceResult(string Period, string? Book, string Currency, IReadOnlyList<TrialBalanceRow> Balances, string TotalDebit, string TotalCredit);

/// <summary>One intake exception: a business event suspended with a reason (REQ-FIN-080).</summary>
internal sealed record IntakeExceptionRow(
    Guid EventId, string SourceModule, string EventType, string AggregateType, string AggregateId, string Reason, string? Detail, int Attempts,
    string ReceivedAt, string UpdatedAt);

/// <summary>One accounting period with its state.</summary>
internal sealed record PeriodRow(string Period, string State, bool ClosedForPosting);

internal sealed record OpsPage<T>(IReadOnlyList<T> Items, string? NextCursor, int Limit);

/// <summary>Read side of the finance operations screens: trial balance, intake exception queue, period list. Every query is scoped to the caller's legal entity.</summary>
internal sealed class FinanceOpsReader(DbSession session)
{
    public async Task<TrialBalanceResult> TrialBalanceAsync(LegalEntityId legalEntity, string period, string? book, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<Line>(new CommandDefinition(
            """
            SELECT l.book AS Book, l.account_code AS Account, coalesce(a.name_el, l.account_code) AS NameEl, coalesce(a.name_en, l.account_code) AS NameEn,
                   coalesce(sum(l.amount_functional) FILTER (WHERE l.side = 'DEBIT'), 0) AS Debit,
                   coalesce(sum(l.amount_functional) FILTER (WHERE l.side = 'CREDIT'), 0) AS Credit,
                   max(l.functional_currency) AS Currency
              FROM fin.journal_line l
              JOIN fin.journal_entry j ON j.journal_id = l.journal_id
              JOIN fin.financial_period p ON p.period_id = j.period_id
              LEFT JOIN fin.gl_account a ON a.legal_entity_code = j.legal_entity_code AND a.book = l.book AND a.account_code = l.account_code
             WHERE j.legal_entity_id = @le AND p.period_code = @period AND (@book::text IS NULL OR l.book = @book)
             GROUP BY l.book, l.account_code, a.name_el, a.name_en
             ORDER BY l.book, l.account_code
            """, new { le = legalEntity.Value, period, book }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var items = rows.Select(r => new TrialBalanceRow(r.Book, r.Account, r.NameEl, r.NameEn, Money(r.Debit), Money(r.Credit), Money(r.Debit - r.Credit))).ToList();
        return new TrialBalanceResult(period, book, rows.Select(r => r.Currency).FirstOrDefault() ?? "EUR", items, Money(rows.Sum(r => r.Debit)), Money(rows.Sum(r => r.Credit)));
    }

    public async Task<OpsPage<IntakeExceptionRow>> IntakeExceptionsAsync(LegalEntityId legalEntity, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var offset = Offset(cursor);
        var rows = (await connection.QueryAsync<IntakeExceptionRow>(new CommandDefinition(
            """
            SELECT business_event_id AS EventId, source_module AS SourceModule, event_type AS EventType, aggregate_type AS AggregateType,
                   aggregate_id AS AggregateId, coalesce(exception_reason, '') AS Reason, exception_detail AS Detail, attempts AS Attempts,
                   to_char(received_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"') AS ReceivedAt,
                   to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"') AS UpdatedAt
              FROM fin.business_event
             WHERE legal_entity_id = @le AND status = 'SUSPENDED'
             ORDER BY received_at, business_event_id
             OFFSET @offset LIMIT @take
            """, new { le = legalEntity.Value, offset, take = limit + 1 }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        return Page(rows, offset, limit);
    }

    public async Task<OpsPage<PeriodRow>> PeriodsAsync(LegalEntityId legalEntity, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var offset = Offset(cursor);
        var rows = (await connection.QueryAsync<PeriodRecord>(new CommandDefinition(
            """
            SELECT period AS Period, state AS State FROM (
                SELECT period_code AS period, status AS state FROM fin.financial_period WHERE legal_entity_id = @le
                UNION ALL
                SELECT to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM'), 'OPEN'
                 WHERE NOT EXISTS (SELECT 1 FROM fin.financial_period WHERE legal_entity_id = @le AND period_code = to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM'))
            ) p ORDER BY period DESC OFFSET @offset LIMIT @take
            """, new { le = legalEntity.Value, offset, take = limit + 1 }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .Select(r => new PeriodRow(r.Period, r.State, r.State == "LOCKED")).ToList();
        return Page(rows, offset, limit);
    }

    private static int Offset(string? cursor) =>
        cursor is null ? 0 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : throw new FormatException("Invalid cursor.");

    private static OpsPage<T> Page<T>(List<T> rows, int offset, int limit) =>
        new(rows.Take(limit).ToList(), rows.Count > limit ? (offset + limit).ToString(CultureInfo.InvariantCulture) : null, limit);

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed record Line(string Book, string Account, string NameEl, string NameEn, decimal Debit, decimal Credit, string Currency);

    private sealed record PeriodRecord(string Period, string State);
}
