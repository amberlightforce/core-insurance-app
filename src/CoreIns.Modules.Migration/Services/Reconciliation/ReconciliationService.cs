using System.Globalization;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Migration.Services.Reconciliation;

/// <summary>One legacy control total in a load request (amount as a decimal string).</summary>
internal sealed record ReconControlTotalBody(string Module, string ObjectType, string? Key, string Currency, long Count, string Amount);

internal sealed record ReconLoadBody(string? Scope, IReadOnlyList<ReconControlTotalBody>? Totals);

internal sealed record ReconLoadView(string Scope, int Loaded);

internal sealed record ReconRunBody(string? Scope, Guid? BatchId);

internal sealed record ReconResultView(
    string Module, string ObjectType, string Key, string Currency, long LegacyCount, long CoreCount,
    string LegacyAmount, string CoreAmount, long CountVariance, string AmountVariance, string Status);

internal sealed record ReconReportView(
    Guid RunId, string Scope, Guid? BatchId, DateTimeOffset RunAt, bool Green, int Matched, int VarianceCount,
    IReadOnlyList<string> Variances, IReadOnlyList<ReconResultView> Results);

/// <summary>Status of one compared figure.</summary>
internal static class ReconStatus
{
    public const string Match = "MATCH";
    public const string Variance = "VARIANCE";
    public const string MissingCore = "MISSING_CORE";
    public const string MissingLegacy = "MISSING_LEGACY";
}

/// <summary>
/// Reconciliation engine (BO-MIG-10, REQ-MIG-171..177): compares the legacy control totals loaded for a scope with what each module holds of migrated
/// data, per object type, key and currency, exact to the minor unit. The modules are read only through <see cref="IMigrationReconciliationReader"/>;
/// the engine writes nothing but its own result rows. A BIL-vs-FIN cross-check compares the BIL opening receivable with the FIN receivable account.
/// </summary>
internal sealed class ReconciliationService(
    MigrationDbContext db, IEnumerable<IMigrationReconciliationReader> readers, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    /// <summary>The FIN control account the BIL opening receivable must equal.</summary>
    // TODO(hardening): resolve the receivable control account from FIN posting configuration instead of this constant.
    public const string ReceivableAccount = "GL-1210";

    public const string CrossCheckObjectType = "ReceivableVsLedger";

    private const string DefaultScope = "default";

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public async Task<Result<ReconLoadView>> LoadAsync(ReconLoadBody body, CancellationToken cancellationToken)
    {
        var scope = NormaliseScope(body.Scope);
        if (scope is null)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "scope must be 1 to 64 characters.");
        }

        if (body.Totals is not { Count: > 0 })
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "totals must list at least one control total.");
        }

        var parsed = new Dictionary<(string, string, string, string), (long Count, decimal Amount)>();
        foreach (var t in body.Totals)
        {
            var module = t.Module?.Trim().ToUpperInvariant() ?? string.Empty;
            var currency = t.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
            if (module.Length is 0 or > 8 || string.IsNullOrWhiteSpace(t.ObjectType) || t.ObjectType.Length > 64 || (t.Key?.Length ?? 0) > 64
                || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper) || t.Count < 0)
            {
                return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, $"Control total {t.Module}/{t.ObjectType}: module, objectType, key, currency or count is not valid.");
            }

            if (!decimal.TryParse(t.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || decimal.Round(amount, 2) != amount)
            {
                return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, $"Control total {module}/{t.ObjectType}/{currency}: amount must be a decimal with at most two decimals.");
            }

            if (!parsed.TryAdd((module, t.ObjectType.Trim(), t.Key?.Trim() ?? string.Empty, currency), (t.Count, amount)))
            {
                return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, $"Control total {module}/{t.ObjectType}/{currency} is listed twice.");
            }
        }

        var legalEntity = LegalEntity;
        var existing = await db.ControlTotals.Where(r => r.LegalEntityId == legalEntity && r.Scope == scope).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var actor = context.Actor.ToString();
        foreach (var row in existing)
        {
            row.Active = parsed.ContainsKey((row.ModuleName, row.ObjectType, row.Key, row.Currency));
        }

        foreach (var ((module, objectType, key, currency), (count, amount)) in parsed)
        {
            var row = existing.SingleOrDefault(r => r.ModuleName == module && r.ObjectType == objectType && r.Key == key && r.Currency == currency);
            if (row is null)
            {
                row = new ControlTotalRow { ControlTotalId = Guid.CreateVersion7(), LegalEntityId = legalEntity, Scope = scope, ModuleName = module, ObjectType = objectType, Key = key, Currency = currency };
                db.ControlTotals.Add(row);
            }

            row.Count = count;
            row.Amount = amount;
            row.Active = true;
            row.LoadedBy = actor;
            row.LoadedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ReconLoadView(scope, parsed.Count);
    }

    public async Task<Result<ReconReportView>> RunAsync(ReconRunBody body, CancellationToken cancellationToken)
    {
        var scope = NormaliseScope(body.Scope);
        if (scope is null)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "scope must be 1 to 64 characters.");
        }

        var legalEntity = LegalEntity;
        var legacy = await db.ControlTotals.AsNoTracking().Where(r => r.LegalEntityId == legalEntity && r.Scope == scope && r.Active).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (legacy.Count == 0)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, $"No control totals are loaded for scope '{scope}'.");
        }

        // Read each involved module once (read-only, module contract).
        var core = new Dictionary<string, IReadOnlyList<ReconciliationFigure>>(StringComparer.Ordinal);
        var modules = legacy.Select(l => l.ModuleName).Append("BIL").Append("FIN").Distinct();
        foreach (var module in modules)
        {
            var reader = readers.FirstOrDefault(r => string.Equals(r.ModuleName, module, StringComparison.OrdinalIgnoreCase));
            if (reader is null)
            {
                if (legacy.Any(l => l.ModuleName == module))
                {
                    return DomainError.Of(ModuleCode.MIG, "DEPENDENCY", $"Module {module} has no reconciliation reader registered.");
                }

                continue;
            }

            core[module] = await reader.ReadAsync(body.BatchId, cancellationToken).ConfigureAwait(false);
        }

        var runId = Guid.CreateVersion7();
        var now = clock.Now;
        var actor = context.Actor.ToString();
        var rows = new List<ReconResultRow>();

        ReconResultRow Row(string module, string objectType, string key, string currency, long lc, long cc, decimal la, decimal ca) => new()
        {
            ResultId = Guid.CreateVersion7(), RunId = runId, LegalEntityId = legalEntity, Scope = scope, BatchId = body.BatchId, ModuleName = module, ObjectType = objectType,
            Key = key, Currency = currency, LegacyCount = lc, CoreCount = cc, LegacyAmount = la, CoreAmount = ca, RunBy = actor, RunAt = now,
            Status = lc == cc && la == ca ? ReconStatus.Match : ReconStatus.Variance,
        };

        foreach (var l in legacy.OrderBy(l => l.ModuleName).ThenBy(l => l.ObjectType).ThenBy(l => l.Key).ThenBy(l => l.Currency))
        {
            var figure = core.GetValueOrDefault(l.ModuleName)?.FirstOrDefault(f => f.ObjectType == l.ObjectType && f.Key == l.Key && f.Currency == l.Currency);
            var row = Row(l.ModuleName, l.ObjectType, l.Key, l.Currency, l.Count, figure?.Count ?? 0, l.Amount, figure?.Amount ?? 0m);
            if (figure is null && (l.Count != 0 || l.Amount != 0m))
            {
                row.Status = ReconStatus.MissingCore;
            }

            rows.Add(row);
        }

        // Core holds migrated figures the legacy totals do not list (whole-object-type rows only, for object types the scope covers).
        var covered = legacy.Select(l => (l.ModuleName, l.ObjectType)).ToHashSet();
        foreach (var (module, figures) in core)
        {
            foreach (var f in figures.Where(f => f.Key.Length == 0 && covered.Contains((module, f.ObjectType))
                && !legacy.Any(l => l.ModuleName == module && l.ObjectType == f.ObjectType && l.Key.Length == 0 && l.Currency == f.Currency)))
            {
                if (f.Count == 0 && f.Amount == 0m)
                {
                    continue;
                }

                var row = Row(module, f.ObjectType, string.Empty, f.Currency, 0, f.Count, 0m, f.Amount);
                row.Status = ReconStatus.MissingLegacy;
                rows.Add(row);
            }
        }

        // BIL sub-ledger vs FIN control account, per currency (follow-up to NX-EC-10). Left = BIL, right = FIN.
        var bilScoped = legacy.Any(l => l.ModuleName == "BIL" && l.ObjectType == "Receivable");
        var finScoped = legacy.Any(l => l.ModuleName == "FIN" && l.ObjectType == "OpeningBalance" && l.Key == ReceivableAccount);
        if (bilScoped && finScoped)
        {
            var bil = core.GetValueOrDefault("BIL")?.Where(f => f.ObjectType == "Receivable" && f.Key.Length == 0).ToList() ?? [];
            var fin = core.GetValueOrDefault("FIN")?.Where(f => f.ObjectType == "OpeningBalance" && f.Key == ReceivableAccount).ToList() ?? [];
            foreach (var currency in bil.Select(f => f.Currency).Union(fin.Select(f => f.Currency)).Order(StringComparer.Ordinal))
            {
                rows.Add(Row("BIL/FIN", CrossCheckObjectType, ReceivableAccount, currency, 0, 0,
                    bil.FirstOrDefault(f => f.Currency == currency)?.Amount ?? 0m, fin.FirstOrDefault(f => f.Currency == currency)?.Amount ?? 0m));
            }
        }

        db.ReconResults.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToReport(rows);
    }

    public async Task<Result<ReconReportView>> ResultsAsync(Guid? runId, string? scope, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var id = runId;
        if (id is null)
        {
            var normalised = NormaliseScope(scope);
            if (normalised is null)
            {
                return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "scope must be 1 to 64 characters.");
            }

            var latest = await db.ReconResults.AsNoTracking().Where(r => r.LegalEntityId == legalEntity && r.Scope == normalised)
                .OrderByDescending(r => r.RunAt).ThenByDescending(r => r.ResultId).Select(r => (Guid?)r.RunId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            id = latest;
        }

        var rows = id is null ? [] : await db.ReconResults.AsNoTracking().Where(r => r.LegalEntityId == legalEntity && r.RunId == id)
            .OrderBy(r => r.ModuleName).ThenBy(r => r.ObjectType).ThenBy(r => r.Key).ThenBy(r => r.Currency).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? MigrationErrors.NotFound("The reconciliation run") : ToReport(rows);
    }

    private static string? NormaliseScope(string? scope)
    {
        var s = string.IsNullOrWhiteSpace(scope) ? DefaultScope : scope.Trim();
        return s.Length is >= 1 and <= 64 ? s : null;
    }

    private static ReconReportView ToReport(List<ReconResultRow> rows)
    {
        var first = rows[0];
        var variances = rows.Where(r => r.Status != ReconStatus.Match).Select(r => $"{r.ModuleName}/{r.ObjectType}/{r.Key}/{r.Currency}").ToList();
        var results = rows.Select(r => new ReconResultView(
            r.ModuleName, r.ObjectType, r.Key, r.Currency, r.LegacyCount, r.CoreCount, Format(r.LegacyAmount), Format(r.CoreAmount),
            r.CoreCount - r.LegacyCount, Format(r.CoreAmount - r.LegacyAmount), r.Status)).ToList();
        return new ReconReportView(first.RunId, first.Scope, first.BatchId, first.RunAt.ToDateTimeOffset(), variances.Count == 0, rows.Count - variances.Count, variances.Count, variances, results);
    }

    private static string Format(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);
}
