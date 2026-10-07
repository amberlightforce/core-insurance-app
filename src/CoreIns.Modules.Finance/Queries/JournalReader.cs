using System.Data;
using System.Globalization;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;

namespace CoreIns.Modules.Finance.Queries;

/// <summary>Filters of <c>fin.Journal.query</c> (slice subset of REQ-FIN-078).</summary>
internal sealed record JournalFilter(
    string? PolicyNumber, string? SourceEventType, string? Book, BusinessDate? From, BusinessDate? To, Instant KnownAt);

/// <summary>
/// The read side of the journals (REQ-FIN-078, -079): Dapper over the scope's connection. Every query is filtered by the
/// caller's legal entity; another entity's journal is "not found". <c>knownAt</c> hides journals posted later (journals
/// are append-only, so the record time of a journal is its posting time).
/// </summary>
internal sealed class JournalReader(DbSession session)
{
    private const string HeaderColumns = """
        j.journal_id AS JournalId, j.journal_number AS JournalNumber, j.legal_entity_code AS LegalEntityCode, j.jurisdiction AS Jurisdiction,
        j.book AS Book, j.accounting_date::text AS AccountingDate, j.business_date::text AS BusinessDate, p.period_code AS PeriodCode,
        j.source_type AS SourceType, j.source_module AS SourceModule, j.source_event_type AS SourceEventType, j.source_event_ids AS SourceEventIds,
        j.source_ref AS SourceRef, j.rule_set_id AS RuleSetId, j.rule_set_version AS RuleSetVersion, j.rule_codes AS RuleCodes,
        j.reverses_journal_id AS ReversesJournalId,
        (SELECT r.journal_id FROM fin.journal_entry r WHERE r.reverses_journal_id = j.journal_id AND r.posted_at <= @knownAt) AS ReversedByJournalId,
        j.posted_at AS PostedAt
        """;

    public async Task<JournalView?> GetAsync(LegalEntityId legalEntity, string idOrNumber, Instant knownAt, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        Guid? id = Guid.TryParse(idOrNumber, out var parsed) ? parsed : null;
        var header = await connection.QuerySingleOrDefaultAsync<HeaderRecord>(new CommandDefinition(
            $"""
             SELECT {HeaderColumns}
               FROM fin.journal_entry j JOIN fin.financial_period p ON p.period_id = j.period_id
              WHERE j.legal_entity_id = @le AND j.posted_at <= @knownAt
                AND ((@id::uuid IS NOT NULL AND j.journal_id = @id) OR (@id::uuid IS NULL AND j.journal_number = @number))
             """, new { le = legalEntity.Value, id, number = idOrNumber, knownAt = knownAt.ToUtcDateTime() }, session.Transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        return (await WithLinesAsync(connection, [header], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<JournalQueryPage> QueryAsync(LegalEntityId legalEntity, JournalFilter filter, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var after = DecodeCursor(cursor);
        var args = new DynamicParameters(new
        {
            le = legalEntity.Value,
            policy = filter.PolicyNumber,
            sourceEventType = filter.SourceEventType,
            book = filter.Book,
            knownAt = filter.KnownAt.ToUtcDateTime(),
            afterNumber = after?.Number,
            take = limit + 1,
        });
        args.Add("from", filter.From?.Value, DbType.Date);
        args.Add("to", filter.To?.Value, DbType.Date);
        args.Add("afterDateValue", after is null ? null : DateOnly.ParseExact(after.Value.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), DbType.Date);
        var headers = (await connection.QueryAsync<HeaderRecord>(new CommandDefinition(
            $"""
             SELECT {HeaderColumns}
               FROM fin.journal_entry j JOIN fin.financial_period p ON p.period_id = j.period_id
              WHERE j.legal_entity_id = @le AND j.posted_at <= @knownAt
                AND (@book::text IS NULL OR j.book = @book)
                AND (@sourceEventType::text IS NULL OR j.source_event_type = @sourceEventType)
                AND (@from::date IS NULL OR j.accounting_date >= @from)
                AND (@to::date IS NULL OR j.accounting_date <= @to)
                AND (@policy::text IS NULL OR EXISTS (SELECT 1 FROM fin.journal_line l WHERE l.journal_id = j.journal_id AND l.policy_number = @policy))
                AND (@afterDateValue::date IS NULL OR (j.accounting_date, j.journal_number) > (@afterDateValue, @afterNumber::text))
              ORDER BY j.accounting_date, j.journal_number
              LIMIT @take
             """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        string? next = null;
        if (headers.Count > limit)
        {
            headers.RemoveAt(limit);
            var last = headers[^1];
            next = EncodeCursor(last.AccountingDate, last.JournalNumber);
        }

        var views = await WithLinesAsync(connection, headers, cancellationToken).ConfigureAwait(false);
        return new JournalQueryPage { Items = [.. views.Select(v => new JournalQueryItem { Journal = v })], NextCursor = next, Limit = limit };
    }

    private async Task<IReadOnlyList<JournalView>> WithLinesAsync(Npgsql.NpgsqlConnection connection, IReadOnlyList<HeaderRecord> headers, CancellationToken cancellationToken)
    {
        if (headers.Count == 0)
        {
            return [];
        }

        var lines = (await connection.QueryAsync<LineRecord>(new CommandDefinition(
            """
            SELECT l.journal_id AS JournalId, l.line_no AS LineNo, l.account_code AS Account, a.name_el AS NameEl, a.name_en AS NameEn, a.code_origin AS CodeOrigin, l.side AS Side,
                   l.amount AS Amount, l.currency AS Currency, l.amount_functional AS AmountFunctional, l.functional_currency AS FunctionalCurrency,
                   l.rule_code AS RuleCode, l.product_code AS ProductCode, l.product_version AS ProductVersion, l.coverage_code AS CoverageCode,
                   l.charge_type AS ChargeType, l.charge_category AS ChargeCategory, l.gl_key AS GlKey, l.policy_id AS PolicyId,
                   l.policy_number AS PolicyNumber, l.policy_term_id AS PolicyTermId, l.policy_transaction_id AS PolicyTransactionId,
                   l.charge_id AS ChargeId, l.billing_account_id AS BillingAccountId, l.invoice_id AS InvoiceId, l.receipt_id AS ReceiptId
              FROM fin.journal_line l
              JOIN fin.journal_entry j ON j.journal_id = l.journal_id
              LEFT JOIN fin.gl_account a ON a.legal_entity_code = j.legal_entity_code AND a.book = l.book AND a.account_code = l.account_code
             WHERE l.journal_id = ANY(@ids)
             ORDER BY l.journal_id, l.line_no
            """, new { ids = headers.Select(h => h.JournalId).ToArray() }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToLookup(l => l.JournalId);

        return [.. headers.Select(h =>
        {
            var journalLines = lines[h.JournalId].Select(ToView).ToList();
            return new JournalView
            {
                JournalId = new JournalId(h.JournalId),
                JournalNumber = JournalNumber.Parse(h.JournalNumber),
                LegalEntity = h.LegalEntityCode,
                Jurisdiction = h.Jurisdiction,
                Book = h.Book,
                AccountingDate = BusinessDate.Parse(h.AccountingDate),
                BusinessDate = BusinessDate.Parse(h.BusinessDate),
                Period = h.PeriodCode,
                SourceType = h.SourceType == "REVERSAL" ? JournalView.SourceTypeValue.Reversal : JournalView.SourceTypeValue.Event,
                SourceModule = h.SourceModule,
                SourceEventType = h.SourceEventType,
                SourceEventIds = h.SourceEventIds,
                SourceRef = h.SourceRef,
                RuleSetId = h.RuleSetId,
                RuleSetVersion = h.RuleSetVersion,
                RuleCodes = h.RuleCodes,
                ReversesJournalId = h.ReversesJournalId,
                ReversedByJournalId = h.ReversedByJournalId,
                PostedAt = CoreIns.Platform.Persistence.InstantConverter.InstantFromDatabase(h.PostedAt),
                Totals = [.. journalLines.Where(l => l.Side == JournalLineView.SideValue.Debit).GroupBy(l => l.Amount.Currency)
                    .OrderBy(g => g.Key.Code, StringComparer.Ordinal).Select(g => Money.Sum(g.Select(l => l.Amount), g.Key))],
                Lines = journalLines,
            };
        })];
    }

    private static JournalLineView ToView(LineRecord l) => new()
    {
        LineNo = l.LineNo,
        Account = l.Account,
        AccountName = new FinLocalizedText { El = l.NameEl ?? l.Account, En = l.NameEn ?? l.Account },
        AccountOrigin = Origin(l.CodeOrigin) ?? FinAccountCodeOrigin.TechnicalPlaceholder,
        Side = l.Side == "DEBIT" ? JournalLineView.SideValue.Debit : JournalLineView.SideValue.Credit,
        Amount = Minor(l.Amount, l.Currency),
        FunctionalAmount = Minor(l.AmountFunctional, l.FunctionalCurrency),
        RuleCode = l.RuleCode,
        Dimensions = new JournalLineDimensions
        {
            ProductCode = l.ProductCode,
            ProductVersion = l.ProductVersion,
            CoverageCode = l.CoverageCode,
            ChargeType = l.ChargeType,
            ChargeCategory = l.ChargeCategory,
            GlKey = l.GlKey,
            PolicyId = l.PolicyId is { } policy ? new PolicyId(policy) : null,
            PolicyNumber = l.PolicyNumber is { } number ? CoreIns.SharedKernel.Identifiers.PolicyNumber.Parse(number) : null,
            PolicyTermId = l.PolicyTermId is { } term ? new PolicyTermId(term) : null,
            PolicyTransactionId = l.PolicyTransactionId,
            ChargeId = l.ChargeId is { } charge ? new ChargeId(charge) : null,
            BillingAccountId = l.BillingAccountId is { } account ? new BillingAccountId(account) : null,
            InvoiceId = l.InvoiceId is { } invoice ? new InvoiceId(invoice) : null,
            ReceiptId = l.ReceiptId,
        },
    };

    /// <summary>numeric(19,4) back to the currency's minor units: exact, because FIN only stores amounts rounded to them.</summary>
    private static Money Minor(decimal amount, string currencyCode)
    {
        var currency = Currency.FromCode(currencyCode.Trim());
        return new Money(decimal.Round(amount, currency.MinorUnits), currency);
    }

    /// <summary>The chart's code origin (PRD-09 illustrative code or technical placeholder).</summary>
    internal static FinAccountCodeOrigin? Origin(string? code) => code switch
    {
        "PRD09_ILLUSTRATIVE" => FinAccountCodeOrigin.Prd09Illustrative,
        "TECHNICAL_PLACEHOLDER" => FinAccountCodeOrigin.TechnicalPlaceholder,
        _ => null,
    };

    private static string EncodeCursor(string date, string number) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{date}|{number}"));

    private static (string Date, string Number)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return parts.Length == 2 && DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? (parts[0], parts[1])
                : throw new FormatException("Invalid cursor.");
        }
        catch (FormatException)
        {
            throw new FormatException("Invalid cursor.");
        }
    }

    private sealed class HeaderRecord
    {
        public Guid JournalId { get; set; }

        public string JournalNumber { get; set; } = string.Empty;

        public string LegalEntityCode { get; set; } = string.Empty;

        public string Jurisdiction { get; set; } = string.Empty;

        public string Book { get; set; } = string.Empty;

        public string AccountingDate { get; set; } = string.Empty;

        public string BusinessDate { get; set; } = string.Empty;

        public string PeriodCode { get; set; } = string.Empty;

        public string SourceType { get; set; } = string.Empty;

        public string SourceModule { get; set; } = string.Empty;

        public string SourceEventType { get; set; } = string.Empty;

        public Guid[] SourceEventIds { get; set; } = [];

        public string SourceRef { get; set; } = string.Empty;

        public Guid RuleSetId { get; set; }

        public int RuleSetVersion { get; set; }

        public string[] RuleCodes { get; set; } = [];

        public Guid? ReversesJournalId { get; set; }

        public Guid? ReversedByJournalId { get; set; }

        public DateTime PostedAt { get; set; }
    }

    private sealed class LineRecord
    {
        public Guid JournalId { get; set; }

        public int LineNo { get; set; }

        public string Account { get; set; } = string.Empty;

        public string? NameEl { get; set; }

        public string? NameEn { get; set; }

        public string? CodeOrigin { get; set; }

        public string Side { get; set; } = string.Empty;

        public decimal Amount { get; set; }

        public string Currency { get; set; } = string.Empty;

        public decimal AmountFunctional { get; set; }

        public string FunctionalCurrency { get; set; } = string.Empty;

        public string RuleCode { get; set; } = string.Empty;

        public string? ProductCode { get; set; }

        public string? ProductVersion { get; set; }

        public string? CoverageCode { get; set; }

        public string? ChargeType { get; set; }

        public string? ChargeCategory { get; set; }

        public string? GlKey { get; set; }

        public Guid? PolicyId { get; set; }

        public string? PolicyNumber { get; set; }

        public Guid? PolicyTermId { get; set; }

        public Guid? PolicyTransactionId { get; set; }

        public Guid? ChargeId { get; set; }

        public Guid? BillingAccountId { get; set; }

        public Guid? InvoiceId { get; set; }

        public Guid? ReceiptId { get; set; }
    }
}
