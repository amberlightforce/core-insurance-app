using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Finance.Queries;

/// <summary>
/// Read side of <c>fin.WrittenPremium.query</c> (REQ-FIN-306): the written premium of record is the net credit on the LRC account (the same definition the
/// earning engine uses), excluding the earning run's own journals, per accounting month, book and currency; newest month first. Offset cursor.
/// TODO(hardening): <c>explainedDifferencesDat</c> stays empty until DAT publishes a written-premium figure to compare against (W8-FIN-01).
/// </summary>
internal sealed class WrittenPremiumReader(DbSession session)
{
    public async Task<WrittenPremiumQueryPage> QueryAsync(LegalEntityId legalEntity, string? period, string? productCode, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
        {
            throw new FormatException("cursor is malformed.");
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<Total>(new CommandDefinition(
            """
            SELECT p.period_code AS Period, l.book AS Book, trim(l.functional_currency) AS Currency,
                   coalesce(sum(l.amount_functional) FILTER (WHERE l.side = 'CREDIT'), 0) AS Credit,
                   coalesce(sum(l.amount_functional) FILTER (WHERE l.side = 'DEBIT'), 0) AS Debit,
                   count(DISTINCT j.journal_id)::int AS Journals
              FROM fin.journal_line l
              JOIN fin.journal_entry j ON j.journal_id = l.journal_id
              JOIN fin.financial_period p ON p.period_id = j.period_id
             WHERE j.legal_entity_id = @le AND l.account_code = @account AND j.source_event_type <> @earning
               AND (@period::text IS NULL OR p.period_code = @period) AND (@product::text IS NULL OR l.product_code = @product)
             GROUP BY p.period_code, l.book, trim(l.functional_currency)
             ORDER BY p.period_code DESC, l.book, trim(l.functional_currency)
             OFFSET @offset LIMIT @take
            """,
            new { le = legalEntity.Value, account = Earning.LrcAccount, earning = Earning.SourceEventType, period, product = productCode, offset, take = limit + 1 },
            session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var more = rows.Count > limit;
        var items = rows.Take(limit).Select(r => new WrittenPremiumQueryItem
        {
            Totals = JsonSerializer.SerializeToElement(new
            {
                period = r.Period,
                book = r.Book,
                currency = r.Currency,
                productCode,
                written = (r.Credit - r.Debit).ToString("0.00", CultureInfo.InvariantCulture),
                credit = r.Credit.ToString("0.00", CultureInfo.InvariantCulture),
                debit = r.Debit.ToString("0.00", CultureInfo.InvariantCulture),
                journals = r.Journals,
            }),
            ExplainedDifferencesDat = JsonSerializer.SerializeToElement(Array.Empty<object>()),
        }).ToList();
        return new WrittenPremiumQueryPage { Items = items, NextCursor = more ? (offset + limit).ToString(CultureInfo.InvariantCulture) : null, Limit = limit };
    }

    private sealed class Total
    {
        public string Period { get; set; } = string.Empty;

        public string Book { get; set; } = string.Empty;

        public string Currency { get; set; } = string.Empty;

        public decimal Credit { get; set; }

        public decimal Debit { get; set; }

        public int Journals { get; set; }
    }
}
