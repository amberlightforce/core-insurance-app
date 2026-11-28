using System.Data;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Services;

/// <summary>What the engine worked out for one policy term in one book and currency.</summary>
internal sealed record TermEarning(
    Guid TermId, Guid PolicyId, string PolicyNumber, string? ProductCode, string? ProductVersion, string Book, string Currency,
    DateOnly Start, DateOnly End, decimal Gross, decimal Net, int DaysEarned, int TermDays, decimal Previous, decimal Earned,
    IReadOnlyList<(string? Coverage, decimal Amount)> Parts)
{
    public decimal Delta => Earned - Previous;
}

/// <summary>
/// The earning engine (BO-FIN-06a, REQ-FIN-003): earned and unearned premium (UPR) per policy term from the premium FIN has posted.
/// Written premium is the net credit on the LRC account (GL-2110) per term; earning moves the earned part to insurance revenue
/// (GL-4110) as one sealed, balanced journal per book dated the as-of date, in an open period only. The calculation is cumulative
/// and idempotent: it earns the difference to everything earlier runs earned, so a rerun posts only what changed (nothing, when
/// nothing did). Simulation runs the same calculation and writes nothing.
/// </summary>
internal sealed class EarningEngine(
    FinanceDbContext db, DbSession session, ReferenceData reference, FinancePeriods periods, JournalWriter writer, IEventPublisher events,
    RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
{
    private static readonly DomainError NoEntity = DomainError.Of(ModuleCode.FIN, "VALIDATION", "The legal entity is not served by this stamp.");

    /// <summary>Runs (posts) or simulates (writes nothing) the earning up to and including <paramref name="asOf"/>.</summary>
    public async Task<Result<EarningRunResponse>> RunAsync(BusinessDate asOf, bool simulate, CancellationToken cancellationToken)
    {
        var scope = ManualJournalScopeOf();
        if (scope is not { } entity || context.LegalEntity is not { } code)
        {
            return NoEntity;
        }

        var legalEntity = entity.Id.Value;
        var profile = await reference.BookProfileAsync(code.Value, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "The legal entity has no book profile.");
        }

        var period = FinancePeriods.CodeOf(asOf);
        if (!simulate)
        {
            if (await periods.StateAsync(legalEntity, period, cancellationToken).ConfigureAwait(false) != FinancePeriods.Open)
            {
                return DomainError.Of(ModuleCode.FIN, "PERIOD-LOCKED", $"Period {period} is not open; earned premium is posted into an open period only.");
            }

            // One run at a time per legal entity until commit, so two requests cannot both earn the same difference.
            await reference.LockAsync($"earning:{legalEntity:N}", cancellationToken).ConfigureAwait(false);
            var latest = await db.EarningRuns.AsNoTracking().Where(r => r.LegalEntityId == legalEntity).OrderByDescending(r => r.AsOf).Select(r => (BusinessDate?)r.AsOf).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (latest is { } last && last.Value > asOf.Value)
            {
                return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"Premium is already earned up to {last}; an earlier as-of date cannot be run again.");
            }
        }

        var startedAt = clock.Now;
        var (terms, skipped) = await ComputeAsync(legalEntity, asOf.Value, profile, cancellationToken).ConfigureAwait(false);
        var currency = profile.FunctionalCurrency.Code;
        var journalIds = new List<Guid>();
        Guid? runId = null;
        if (!simulate)
        {
            runId = Guid.CreateVersion7();
            foreach (var book in terms.Where(t => t.Delta != 0m).Select(t => t.Book).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var posted = await PostAsync(entity, code.Value, profile, book, asOf, runId.Value, period, terms.Where(t => t.Book == book && t.Delta != 0m).ToList(), cancellationToken).ConfigureAwait(false);
                if (posted.IsFailure)
                {
                    return posted.Error;
                }

                if (posted.Value != Guid.Empty)
                {
                    journalIds.Add(posted.Value);
                }
            }
        }

        var earnedTotal = terms.Sum(t => t.Earned);
        var deltaTotal = terms.Sum(t => t.Delta);
        var finishedAt = clock.Now;
        if (runId is { } id)
        {
            db.EarningRuns.Add(new EarningRunRow
            {
                RunId = id, LegalEntityId = legalEntity, PeriodCode = period, AsOf = asOf, State = Earning.Done, StartedAt = startedAt, FinishedAt = finishedAt,
                RunBy = context.Actor.Id, PoliciesProcessed = terms.Count, TermsSkipped = skipped, Currency = currency, EarnedToDate = earnedTotal, PostedDelta = deltaTotal, JournalIds = [.. journalIds],
            });
            db.EarnedPremiums.AddRange(terms.Select(t => new EarnedPremiumRow
            {
                RunId = id, LegalEntityId = legalEntity, PolicyTermId = t.TermId, Book = t.Book, Currency = t.Currency, PolicyId = t.PolicyId, PolicyNumber = t.PolicyNumber,
                TermStart = new BusinessDate(t.Start), TermEnd = new BusinessDate(t.End), WrittenGross = t.Gross, WrittenNet = t.Net, DaysEarned = t.DaysEarned, TermDays = t.TermDays,
                EarnedToDate = t.Earned, Delta = t.Delta,
            }));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var view = new EarningRunView
        {
            Id = runId,
            Period = period,
            AsOf = asOf,
            State = simulate ? EarningRunView.StateValue.Simulated : EarningRunView.StateValue.Done,
            Simulated = simulate,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            PoliciesProcessed = terms.Count,
            TermsSkipped = skipped,
            EarnedToDate = [Money.Of(earnedTotal, currency)],
            PostedDelta = [Money.Of(deltaTotal, currency)],
            JournalIds = [.. journalIds.Select(j => new JournalId(j))],
        };
        var shown = terms.Where(t => t.Delta != 0m || simulate).Take(Earning.InlineResults + 1).ToList();
        return new EarningRunResponse
        {
            Run = view,
            Results = [.. shown.Take(Earning.InlineResults).Select(ToResult)],
            ResultsTruncated = shown.Count > Earning.InlineResults,
        };
    }

    /// <summary>The latest earning run of the legal entity, or null before the first one.</summary>
    public async Task<Result<EarningStatusResponse>> StatusAsync(CancellationToken cancellationToken)
    {
        if (ManualJournalScopeOf() is not { } entity)
        {
            return NoEntity;
        }

        var run = await new Queries.EarningReader(db).LatestAsync(entity.Id.Value, cancellationToken).ConfigureAwait(false);
        return run is null ? new EarningStatusResponse() : new EarningStatusResponse { Run = run };
    }

    private (LegalEntityId Id, LegalEntityCode Code)? ManualJournalScopeOf() => Commands.ManualJournalScope.Of(context, legalEntities);

    private static EarningTermResult ToResult(TermEarning t) => new()
    {
        PolicyTermId = new PolicyTermId(t.TermId),
        PolicyNumber = t.PolicyNumber,
        Book = t.Book,
        TermStart = new BusinessDate(t.Start),
        TermEnd = new BusinessDate(t.End),
        WrittenPremium = Money.Of(t.Net, t.Currency),
        EarnedToDate = Money.Of(t.Earned, t.Currency),
        PreviouslyEarned = Money.Of(t.Previous, t.Currency),
        Delta = Money.Of(t.Delta, t.Currency),
        DaysEarned = t.DaysEarned,
        TermDays = t.TermDays,
    };

    private async Task<Result<Guid>> PostAsync(
        (LegalEntityId Id, LegalEntityCode Code) entity, string code, BookProfile profile, string book, BusinessDate asOf, Guid runId, string period,
        IReadOnlyList<TermEarning> terms, CancellationToken cancellationToken)
    {
        var setup = await reference.BookSetupAsync(profile, book, asOf, cancellationToken).ConfigureAwait(false);
        if (setup is null || !setup.Accounts.TryGetValue(Earning.RevenueAccount, out var revenue) || !revenue.Active || !setup.Accounts.ContainsKey(Earning.LrcAccount))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"Book {book} has no rule set or no active {Earning.RevenueAccount} / {Earning.LrcAccount} account to earn premium into.");
        }

        if (context.Jurisdiction is not { } jurisdiction)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "The request has no jurisdiction.");
        }

        var lines = new List<JournalLineDraft>();
        foreach (var term in terms.OrderBy(t => t.TermId))
        {
            foreach (var (coverage, amount) in term.Parts.Where(p => p.Amount != 0m))
            {
                var money = Money.Of(Math.Abs(amount), term.Currency);
                var dimensions = new LineDimensions
                {
                    PolicyId = term.PolicyId, PolicyNumber = term.PolicyNumber, PolicyTermId = term.TermId, ProductCode = term.ProductCode, ProductVersion = term.ProductVersion, CoverageCode = coverage,
                };
                var (lrcSide, revenueSide) = amount > 0m ? (Sides.Debit, Sides.Credit) : (Sides.Credit, Sides.Debit);
                lines.Add(new JournalLineDraft(lines.Count + 1, Earning.LrcAccount, lrcSide, money, money, Earning.RuleCode, dimensions));
                lines.Add(new JournalLineDraft(lines.Count + 1, Earning.RevenueAccount, revenueSide, money, money, Earning.RuleCode, dimensions));
            }
        }

        if (lines.Count == 0)
        {
            return Guid.Empty;
        }

        var draft = new JournalDraft(book, asOf, asOf, SourceTypes.Event, setup.RuleSet.RuleSetId, setup.RuleSet.Version, [Earning.RuleCode], lines);
        var source = new JournalSource(entity.Id, code, jurisdiction.Value, "FIN", Earning.SourceEventType, [runId], $"earning:{period}:{runId:N}:{book}");
        var written = await writer.WriteAsync(draft, source, profile.FunctionalCurrency, null, null, cancellationToken).ConfigureAwait(false);
        events.Publish(written.Posted);
        return written.JournalId;
    }

    private async Task<(List<TermEarning> Terms, int Skipped)> ComputeAsync(Guid legalEntity, DateOnly asOf, BookProfile profile, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var args = new DynamicParameters(new { le = legalEntity, account = Earning.LrcAccount, source = Earning.SourceEventType });
        args.Add("asof", asOf, DbType.Date);
        var written = (await connection.QueryAsync<WrittenRecord>(new CommandDefinition(
            """
            SELECT l.book AS Book, l.policy_term_id AS TermId, l.coverage_code AS Coverage, trim(l.currency) AS Currency,
                   COALESCE(SUM(CASE WHEN l.side = 'CREDIT' THEN l.amount END), 0) AS Credit,
                   COALESCE(SUM(CASE WHEN l.side = 'DEBIT' THEN l.amount END), 0) AS Debit
              FROM fin.journal_line l JOIN fin.journal_entry j ON j.journal_id = l.journal_id
             WHERE l.legal_entity_id = @le AND l.account_code = @account AND l.policy_term_id IS NOT NULL
               AND j.source_event_type <> @source AND j.accounting_date <= @asof
             GROUP BY l.book, l.policy_term_id, l.coverage_code, trim(l.currency)
            """, args, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var previous = (await connection.QueryAsync<PreviousRecord>(new CommandDefinition(
            """
            SELECT book AS Book, policy_term_id AS TermId, trim(currency) AS Currency, SUM(delta) AS Earned
              FROM fin.earned_premium WHERE legal_entity_id = @le GROUP BY book, policy_term_id, trim(currency)
            """, new { le = legalEntity }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToDictionary(p => (p.Book, p.TermId, p.Currency));

        var termIds = written.Select(w => w.TermId).Distinct().ToList();
        var contexts = await db.PolicyContexts.AsNoTracking().Where(c => c.LegalEntityId == legalEntity && termIds.Contains(c.PolicyTermId)).ToDictionaryAsync(c => c.PolicyTermId, cancellationToken).ConfigureAwait(false);
        var terms = new List<TermEarning>();
        var skipped = new HashSet<(string, Guid)>();
        foreach (var group in written.GroupBy(w => (w.Book, w.TermId, w.Currency)).OrderBy(g => g.Key.Book, StringComparer.Ordinal).ThenBy(g => g.Key.TermId))
        {
            var (book, termId, currency) = group.Key;
            if (!contexts.TryGetValue(termId, out var term) || term.TermStart is not { } start || term.TermEnd is not { } end
                || Earning.TermDays(start.Value, end.Value) <= 0 || currency != profile.FunctionalCurrency.Code
                || !profile.ActiveBooks.Contains(book, StringComparer.Ordinal))
            {
                // TODO(hardening): non-functional-currency terms need the FX rate the premium was written at; terms without dates wait for a backfill.
                skipped.Add((book, termId));
                continue;
            }

            var gross = group.Sum(g => g.Credit);
            var net = gross - group.Sum(g => g.Debit);
            var termDays = Earning.TermDays(start.Value, end.Value);
            var days = Earning.DaysEarned(start.Value, end.Value, asOf);
            var earned = Earning.EarnedToDate(gross, net, days, termDays);
            var before = previous.TryGetValue((book, termId, currency), out var p) ? p.Earned : 0m;
            if (gross == 0m && net == 0m && before == 0m)
            {
                continue;
            }

            var components = group.OrderBy(g => g.Coverage, StringComparer.Ordinal).Select(g => (g.Coverage, Weight: g.Credit - g.Debit)).ToList();
            terms.Add(new TermEarning(
                termId, term.PolicyId, term.PolicyNumber, term.ProductCode, term.ProductVersion, book, currency, start.Value, end.Value, gross, net, days, termDays, before, earned,
                Earning.SplitByCoverage(earned - before, components)));
        }

        return (terms, skipped.Count);
    }

    private sealed class WrittenRecord
    {
        public string Book { get; set; } = string.Empty;

        public Guid TermId { get; set; }

        public string? Coverage { get; set; }

        public string Currency { get; set; } = string.Empty;

        public decimal Credit { get; set; }

        public decimal Debit { get; set; }
    }

    private sealed class PreviousRecord
    {
        public string Book { get; set; } = string.Empty;

        public Guid TermId { get; set; }

        public string Currency { get; set; } = string.Empty;

        public decimal Earned { get; set; }
    }
}
