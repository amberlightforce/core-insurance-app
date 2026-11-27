using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Finance.Services;

/// <summary>
/// The reconciliation framework (BO-FIN-08, REQ-FIN-244, -250..-259): source control totals are compared per currency with the ledger movement of the
/// definition's accounts in a period. A run is insert-only; breaks carry over between runs (same currency and kind and the same figures keep their
/// state, so a rerun is idempotent), are RESOLVED when a rerun no longer finds them or when matched by hand, and are ACCEPTED at period close.
/// </summary>
internal sealed class ReconciliationService(FinanceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    private static readonly DomainError NoEntity = DomainError.Of(ModuleCode.FIN, "VALIDATION", "The legal entity is not served by this stamp.");

    public async Task<Result<ReconciliationRunResponse>> RunAsync(string? definition, string period, CancellationToken cancellationToken)
    {
        if (!PeriodClose.IsPeriodCode(period))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "period must be yyyy-MM.");
        }

        if (definition is not null && Reconciliation.Find(definition) is null)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"Unknown reconciliation definition {definition}.");
        }

        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        if (await LockedAsync(legalEntity, period, cancellationToken).ConfigureAwait(false) is { } locked)
        {
            return locked;
        }

        var runs = await RunDefinitionsAsync(legalEntity, Reconciliation.Definitions.Where(d => definition is null || d.Code == definition).ToList(), period, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ReconciliationRunResponse { Runs = [.. runs.Select(r => ToView(r.Run, r.Open))] };
    }

    public async Task<Result<ReconciliationExchangeResponse>> ExchangeAsync(ReconciliationExchangeRequest request, CancellationToken cancellationToken)
    {
        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        var source = request.Source switch
        {
            ReconciliationExchangeRequest.SourceValue.Bil => "BIL",
            ReconciliationExchangeRequest.SourceValue.Ri => "RI",
            _ => "CLM",
        };
        var period = string.Create(CultureInfo.InvariantCulture, $"{request.Date.Year:0000}-{request.Date.Month:00}");
        var parsed = new List<(string Currency, decimal Amount, int? Count)>();
        foreach (var total in request.Totals ?? [])
        {
            var currency = total.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper) || !Reconciliation.TryAmount(total.Amount, out var amount) || total.Count < 0 || parsed.Any(p => p.Currency == currency))
            {
                return DomainError.Of(ModuleCode.FIN, "TOTALS-SCHEMA", $"Control total {total.Currency}: currency, amount (at most 2 decimals) or count is not valid, or the currency is repeated.");
            }

            parsed.Add((currency, amount, total.Count));
        }

        if (parsed.Count == 0)
        {
            return DomainError.Of(ModuleCode.FIN, "TOTALS-SCHEMA", "totals must list at least one control total.");
        }

        if (await LockedAsync(legalEntity, period, cancellationToken).ConfigureAwait(false) is { } locked)
        {
            return locked;
        }

        var now = clock.Now;
        var currencies = parsed.Select(p => p.Currency).ToArray();
        var existing = await db.ReconSourceTotals.Where(r => r.LegalEntityId == legalEntity.Value && r.Source == source && r.AsOf == request.Date && currencies.Contains(r.Currency)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (currency, amount, count) in parsed)
        {
            var row = existing.FirstOrDefault(r => r.Currency == currency);
            if (row is null)
            {
                row = new ReconSourceTotalRow { LegalEntityId = legalEntity.Value, Source = source, AsOf = request.Date, PeriodCode = period, Currency = currency };
                db.ReconSourceTotals.Add(row);
            }

            row.Amount = Reconciliation.Money(amount);
            row.ItemCount = count;
            row.ReceivedAt = now;
            row.ReceivedBy = context.Actor.Id;
        }

        // The comparison reads the stored totals, so they are saved first (one transaction with the run through the command pipeline).
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var runs = await RunDefinitionsAsync(legalEntity, Reconciliation.Definitions.Where(d => d.Source == source).ToList(), period, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var open = runs.SelectMany(r => r.Active).Where(b => b.State == Reconciliation.Open).Select(b => b.BreakId).ToList();
        return new ReconciliationExchangeResponse
        {
            ComparisonResult = open.Count == 0 ? ReconciliationExchangeResponse.ComparisonResultValue.Matched : ReconciliationExchangeResponse.ComparisonResultValue.Breaks,
            BreakIds = open,
        };
    }

    public async Task<Result<ReconciliationDetailResponse>> DetailAsync(string period, string? definition, CancellationToken cancellationToken)
    {
        if (!PeriodClose.IsPeriodCode(period) || (definition is not null && Reconciliation.Find(definition) is null))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "period must be yyyy-MM and the definition one that exists.");
        }

        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        var runs = await db.ReconRuns.AsNoTracking().Where(r => r.LegalEntityId == legalEntity.Value && r.PeriodCode == period).OrderByDescending(r => r.RunNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var breaks = await db.ReconBreaks.AsNoTracking().Where(b => b.LegalEntityId == legalEntity.Value && b.PeriodCode == period).Select(b => new { b.DefinitionCode, b.State }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var definitions = new List<ReconDefinitionView>();
        foreach (var def in Reconciliation.Definitions)
        {
            var latest = runs.FirstOrDefault(r => r.DefinitionCode == def.Code);
            var open = breaks.Count(b => b.DefinitionCode == def.Code && b.State == Reconciliation.Open);
            definitions.Add(new ReconDefinitionView
            {
                Code = def.Code,
                Description = def.Description,
                SourceModule = def.Source,
                LedgerAccounts = def.Accounts,
                LatestRun = latest is null ? null : ToView(latest, open),
                OpenBreaks = open,
                AcceptedBreaks = breaks.Count(b => b.DefinitionCode == def.Code && b.State == Reconciliation.Accepted),
            });
        }

        var history = definition is null
            ? []
            : runs.Where(r => r.DefinitionCode == definition).Select(r => ToView(r, r.OpenBreaks)).ToList();
        return new ReconciliationDetailResponse { Period = period, Definitions = definitions, Runs = history };
    }

    public async Task<Result<ReconciliationBreaksPage>> BreaksAsync(string? period, string? definition, string? state, string? cursor, int limit, CancellationToken cancellationToken)
    {
        Guid? before = null;
        if ((period is not null && !PeriodClose.IsPeriodCode(period))
            || (definition is not null && Reconciliation.Find(definition) is null)
            || (state is not null && state is not (Reconciliation.Open or Reconciliation.Accepted or Reconciliation.Resolved)))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "period must be yyyy-MM, definition and state must exist.");
        }

        if (cursor is not null)
        {
            if (!Guid.TryParse(cursor, out var g))
            {
                return DomainError.Of(ModuleCode.FIN, "VALIDATION", "cursor is malformed.");
            }

            before = g;
        }

        if (ResolveEntity() is not { } legalEntity)
        {
            return new ReconciliationBreaksPage { Items = [], NextCursor = null, Limit = limit };
        }

        var query = db.ReconBreaks.AsNoTracking().Where(b => b.LegalEntityId == legalEntity.Value);
        if (period is not null)
        {
            query = query.Where(b => b.PeriodCode == period);
        }

        if (definition is not null)
        {
            query = query.Where(b => b.DefinitionCode == definition);
        }

        if (state is not null)
        {
            query = query.Where(b => b.State == state);
        }

        if (before is { } id)
        {
            query = query.Where(b => b.BreakId.CompareTo(id) < 0);
        }

        var rows = await query.OrderByDescending(b => b.BreakId).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).ToList();
        return new ReconciliationBreaksPage { Items = [.. page.Select(ToView)], NextCursor = rows.Count > limit ? page[^1].BreakId.ToString("D") : null, Limit = limit };
    }

    public async Task<Result<ReconciliationMatchResponse>> MatchAsync(Guid breakId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "A reason of 1 to 1000 characters is required.");
        }

        if (ResolveEntity() is not { } legalEntity)
        {
            return NoEntity;
        }

        var row = await db.ReconBreaks.SingleOrDefaultAsync(b => b.BreakId == breakId && b.LegalEntityId == legalEntity.Value, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The reconciliation break does not exist.");
        }

        if (row.State != Reconciliation.Open)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", $"The break is {row.State}; only an open break is matched.");
        }

        if (await LockedAsync(legalEntity, row.PeriodCode, cancellationToken).ConfigureAwait(false) is { } locked)
        {
            return locked;
        }

        row.State = Reconciliation.Resolved;
        row.Resolution = Reconciliation.Matched;
        row.DecidedBy = context.Actor.Id;
        row.DecidedAt = clock.Now;
        row.Reason = reason.Trim();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ReconciliationMatchResponse { Break = ToView(row) };
    }

    /// <summary>
    /// Accepts every open break of the period for the close (BO-FIN-04 fin.Close.acceptBreak): needs a reason, and the accepter must not be the person
    /// who ran the reconciliation that found a break. Nothing is saved here; the caller commits it together with the task sign-off.
    /// </summary>
    public async Task<DomainError?> AcceptOpenBreaksAsync(Guid legalEntity, string period, string reason, CancellationToken cancellationToken)
    {
        var open = await db.ReconBreaks.Where(b => b.LegalEntityId == legalEntity && b.PeriodCode == period && b.State == Reconciliation.Open).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (open.Count == 0)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"Period {period} has no open reconciliation break to accept.");
        }

        var actor = context.Actor.Id;
        if (open.Any(b => b.LastRunBy == actor))
        {
            return DomainError.Of(ModuleCode.FIN, "APPROVAL-REQUIRED", "A different person than the one who ran the reconciliation must accept its breaks.");
        }

        var now = clock.Now;
        foreach (var row in open)
        {
            row.State = Reconciliation.Accepted;
            row.DecidedBy = actor;
            row.DecidedAt = now;
            row.Reason = reason.Trim();
        }

        return null;
    }

    /// <summary>The number of open breaks of the period, for the RECONCILIATIONS close gate.</summary>
    public Task<int> OpenBreaksAsync(Guid legalEntity, string period, CancellationToken cancellationToken) =>
        db.ReconBreaks.AsNoTracking().CountAsync(b => b.LegalEntityId == legalEntity && b.PeriodCode == period && b.State == Reconciliation.Open, cancellationToken);

    private async Task<List<(ReconRunRow Run, int Open, List<ReconBreakRow> Active)>> RunDefinitionsAsync(
        LegalEntityId legalEntity, IReadOnlyList<ReconDefinition> definitions, string period, CancellationToken cancellationToken)
    {
        var periodRow = await db.Periods.AsNoTracking().SingleOrDefaultAsync(p => p.LegalEntityId == legalEntity.Value && p.PeriodCode == period, cancellationToken).ConfigureAwait(false);
        var results = new List<(ReconRunRow, int, List<ReconBreakRow>)>();
        foreach (var definition in definitions)
        {
            results.Add(await RunOneAsync(legalEntity.Value, definition, period, periodRow?.PeriodId, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<(ReconRunRow Run, int Open, List<ReconBreakRow> Active)> RunOneAsync(
        Guid legalEntity, ReconDefinition definition, string period, Guid? periodId, CancellationToken cancellationToken)
    {
        var sourceRows = await db.ReconSourceTotals.AsNoTracking().Where(r => r.LegalEntityId == legalEntity && r.Source == definition.Source && r.PeriodCode == period).ToListAsync(cancellationToken).ConfigureAwait(false);
        var source = sourceRows.GroupBy(r => r.Currency.Trim()).ToDictionary(
            g => g.Key, g => new Reconciliation.Side(g.Sum(r => r.Amount), g.Any(r => r.ItemCount is null) ? null : g.Sum(r => r.ItemCount!.Value)));

        var accounts = definition.Accounts.ToArray();
        var lines = periodId is not { } pid
            ? []
            : await db.JournalLines.AsNoTracking()
                .Where(l => l.LegalEntityId == legalEntity && accounts.Contains(l.AccountCode) && db.Journals.Any(j => j.JournalId == l.JournalId && j.PeriodId == pid))
                .Select(l => new { l.Side, l.Currency, l.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ledger = lines.GroupBy(l => l.Currency.Trim()).ToDictionary(
            g => g.Key, g => new Reconciliation.Side(definition.Sign * g.Sum(l => l.Side == Sides.Debit ? l.Amount : -l.Amount), g.Count()));

        var found = Reconciliation.Compare(source, ledger);
        var now = clock.Now;
        var runNo = (await db.ReconRuns.AsNoTracking().Where(r => r.LegalEntityId == legalEntity && r.DefinitionCode == definition.Code && r.PeriodCode == period).MaxAsync(r => (int?)r.RunNo, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        var run = new ReconRunRow
        {
            RunId = Guid.CreateVersion7(), LegalEntityId = legalEntity, DefinitionCode = definition.Code, PeriodCode = period, RunNo = runNo, RanBy = context.Actor.Id, RanAt = now,
            Totals = JsonSerializer.Serialize(source.Keys.Union(ledger.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(c =>
            {
                var s = source.GetValueOrDefault(c);
                var l = ledger.GetValueOrDefault(c);
                return new ReconCurrencyTotal
                {
                    Currency = c, SourceAmount = Reconciliation.Text(s?.Amount ?? 0m), LedgerAmount = Reconciliation.Text(l?.Amount ?? 0m),
                    Difference = Reconciliation.Text(Reconciliation.Money(s?.Amount ?? 0m) - Reconciliation.Money(l?.Amount ?? 0m)), SourceCount = s?.Count, LedgerCount = l?.Count ?? 0,
                };
            }).ToList()),
        };

        var active = await db.ReconBreaks.Where(b => b.LegalEntityId == legalEntity && b.DefinitionCode == definition.Code && b.PeriodCode == period && (b.State == Reconciliation.Open || b.State == Reconciliation.Accepted)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var matched = await db.ReconBreaks.AsNoTracking().Where(b => b.LegalEntityId == legalEntity && b.DefinitionCode == definition.Code && b.PeriodCode == period && b.Resolution == Reconciliation.Matched).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.ReconRuns.Add(run);
        var current = new List<ReconBreakRow>();
        foreach (var f in found)
        {
            var same = active.FirstOrDefault(b => b.Currency.Trim() == f.Currency && b.Kind == f.Kind);
            if (same is not null && Same(same, f))
            {
                same.LastRunId = run.RunId;
                same.LastRunBy = run.RanBy;
                current.Add(same);
                continue;
            }

            if (same is not null)
            {
                active.Remove(same);
                if (same.State == Reconciliation.Open)
                {
                    // Not yet decided by anyone: the figures are simply updated.
                    Fill(same, f);
                    same.LastRunId = run.RunId;
                    same.LastRunBy = run.RanBy;
                    current.Add(same);
                    continue;
                }

                // An accepted break whose figures changed is no longer what was accepted.
                same.State = Reconciliation.Resolved;
                same.Resolution = Reconciliation.Superseded;
                same.DecidedAt = now;
                same.Reason = $"{same.Reason} (superseded by run {runNo}: figures changed)";
            }

            if (matched.Any(b => b.Currency.Trim() == f.Currency && b.Kind == f.Kind && Same(b, f)))
            {
                continue;
            }

            var created = new ReconBreakRow
            {
                BreakId = Guid.CreateVersion7(), LegalEntityId = legalEntity, DefinitionCode = definition.Code, PeriodCode = period, Currency = f.Currency, Kind = f.Kind,
                State = Reconciliation.Open, RunId = run.RunId, LastRunId = run.RunId, LastRunBy = run.RanBy, CreatedAt = now,
            };
            Fill(created, f);
            db.ReconBreaks.Add(created);
            current.Add(created);
        }

        foreach (var gone in active.Where(b => !current.Contains(b)))
        {
            gone.State = Reconciliation.Resolved;
            gone.Resolution = Reconciliation.Cleared;
            gone.DecidedAt = now;
            gone.Reason = $"Cleared by run {runNo}: no longer found.";
        }

        var open = current.Count(b => b.State == Reconciliation.Open);
        run.State = open == 0 ? Reconciliation.Clean : Reconciliation.Breaks;
        run.OpenBreaks = open;
        return (run, open, current);
    }

    private static bool Same(ReconBreakRow row, Reconciliation.Found f) =>
        row.SourceAmount == f.SourceAmount && row.LedgerAmount == f.LedgerAmount && row.SourceCount == f.SourceCount && row.LedgerCount == f.LedgerCount;

    private static void Fill(ReconBreakRow row, Reconciliation.Found f)
    {
        row.SourceAmount = f.SourceAmount;
        row.LedgerAmount = f.LedgerAmount;
        row.SourceCount = f.SourceCount;
        row.LedgerCount = f.LedgerCount;
    }

    private async Task<DomainError?> LockedAsync(LegalEntityId legalEntity, string period, CancellationToken cancellationToken)
    {
        var locked = await db.Periods.AsNoTracking().AnyAsync(p => p.LegalEntityId == legalEntity.Value && p.PeriodCode == period && p.Status == PeriodClose.Locked, cancellationToken).ConfigureAwait(false);
        return locked ? DomainError.Of(ModuleCode.FIN, "PERIOD-LOCKED", $"Period {period} is locked; reopen it to reconcile again.") : null;
    }

    private LegalEntityId? ResolveEntity()
    {
        if (context.LegalEntity is not { } code)
        {
            return null;
        }

        try
        {
            return legalEntities.Resolve(code);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static ReconRunView ToView(ReconRunRow run, int open) => new()
    {
        RunId = run.RunId,
        Definition = run.DefinitionCode,
        Period = run.PeriodCode,
        RunNo = run.RunNo,
        State = run.State == Reconciliation.Clean ? ReconRunView.StateValue.Clean : ReconRunView.StateValue.Breaks,
        RanBy = run.RanBy,
        RanAt = run.RanAt,
        OpenBreaks = open,
        Currencies = JsonSerializer.Deserialize<List<ReconCurrencyTotal>>(run.Totals) ?? [],
    };

    private static ReconBreakView ToView(ReconBreakRow b) => new()
    {
        BreakId = b.BreakId,
        RunId = b.RunId,
        LastRunId = b.LastRunId,
        Definition = b.DefinitionCode,
        Period = b.PeriodCode,
        Currency = b.Currency.Trim(),
        Kind = b.Kind switch
        {
            Reconciliation.Count => ReconBreakView.KindValue.Count,
            Reconciliation.MissingSource => ReconBreakView.KindValue.MissingSource,
            Reconciliation.MissingLedger => ReconBreakView.KindValue.MissingLedger,
            _ => ReconBreakView.KindValue.Amount,
        },
        State = b.State switch
        {
            Reconciliation.Accepted => ReconBreakView.StateValue.Accepted,
            Reconciliation.Resolved => ReconBreakView.StateValue.Resolved,
            _ => ReconBreakView.StateValue.Open,
        },
        SourceAmount = Reconciliation.Text(b.SourceAmount),
        LedgerAmount = Reconciliation.Text(b.LedgerAmount),
        Difference = Reconciliation.Text(b.SourceAmount - b.LedgerAmount),
        SourceCount = b.SourceCount,
        LedgerCount = b.LedgerCount,
        CreatedAt = b.CreatedAt,
        DecidedBy = b.DecidedBy,
        DecidedAt = b.DecidedAt,
        Reason = b.Reason,
    };
}
