using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.MigrationImport;

/// <summary><c>clm.Import.claim</c> (BO-CLM-14): one legacy claim through <see cref="ClaimMigrationImporter"/>.</summary>
internal sealed record ImportClaim(ImportClaimRequest Request) : ICommand<ImportClaimResponse>;

/// <summary><c>clm.Import.financialHistory</c>: the legacy reserve movements of an imported claim as migrated ledger lines.</summary>
internal sealed record ImportFinancialHistory(ImportFinancialHistoryRequest Request) : ICommand<ImportFinancialHistoryResponse>;

/// <summary><c>clm.Import.convertCurrency</c>: currency changeover plan, previewed on the open reserve lines.</summary>
internal sealed record ImportConvertCurrency(ImportConvertCurrencyRequest Request) : ICommand<ImportConvertCurrencyResponse>;

/// <summary><c>clm.Import.reverse</c>: removes the claims of one migration batch through <see cref="ClaimMigrationReverser"/>.</summary>
internal sealed record ImportReverse(ImportReverseRequest Request) : ICommand<ImportReverseResponse>;

internal static class ImportJson
{
    public static DomainError Invalid(string message) => DomainError.Of(ModuleCode.CLM, "IMPORT-VALIDATION", message);

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static JsonElement Of(object value) => JsonSerializer.SerializeToElement(value, SharedKernelJson.Options);
}

internal sealed class ImportClaimHandler(IEnumerable<IMigrationImporter> importers, ClaimsDbContext db, ClaimProtection protection, RequestContext context)
    : ICommandHandler<ImportClaim, ImportClaimResponse>
{
    public async Task<Result<ImportClaimResponse>> HandleAsync(ImportClaim command, CancellationToken cancellationToken)
    {
        var legacy = command.Request.LegacyClaim;
        if (legacy is not { ValueKind: JsonValueKind.Object } payload)
        {
            return ImportJson.Invalid("legacyClaim is required.");
        }

        var number = ImportJson.Text(payload, "legacyNumber");
        if (string.IsNullOrWhiteSpace(number) || !payload.TryGetProperty("batchId", out var batchProperty) || !batchProperty.TryGetGuid(out var batchId) || batchId == Guid.Empty)
        {
            return ImportJson.Invalid("legacyClaim needs legacyNumber and batchId.");
        }

        // Importing twice answers the claim of the first import (idempotent by legacy number).
        var entity = protection.Current(context);
        var existing = await db.MigrationImports.AsNoTracking().SingleOrDefaultAsync(m => m.LegalEntityId == entity && m.LegacyNumber == number, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new ImportClaimResponse { Claim = await ViewAsync(existing.ClaimId, number, existing.BatchId, false, cancellationToken).ConfigureAwait(false) };
        }

        var importer = importers.OfType<ClaimMigrationImporter>().Single();
        var outcome = await importer.ImportAsync(new MigrationImportItem(ClaimMigrationImporter.ObjectType, number, payload), new MigrationImportContext(batchId, context.DryRun, null), cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status == ImportStatus.Failed)
        {
            return ImportJson.Invalid($"{outcome.ErrorCode}: {outcome.Message}");
        }

        var view = outcome.CoreId is { } id
            ? await ViewAsync(new ClaimId(id), number, batchId, true, cancellationToken).ConfigureAwait(false)
            : ImportJson.Of(new { legacyNumber = number, batchId, imported = false, dryRun = true, message = outcome.Message });
        return new ImportClaimResponse { Claim = view };
    }

    private async Task<JsonElement> ViewAsync(ClaimId claimId, string legacyNumber, Guid batchId, bool imported, CancellationToken cancellationToken)
    {
        var claim = await db.Claims.AsNoTracking().SingleAsync(c => c.ClaimId == claimId, cancellationToken).ConfigureAwait(false);
        return ImportJson.Of(new { claimId = claimId.Value, claimNumber = claim.ClaimNumber.Value, legacyNumber, batchId, imported, status = claim.Status });
    }
}

/// <summary>
/// Posts the legacy reserve movements of an imported claim as ledger lines of one approved set (submitter = the migration actor), the
/// functional amount at the legacy rate. The ledger is append-only (D-ARC-34), so a claim with posted history is no longer reversible:
/// the reverser reports it blocked. Posting twice is a no-op (the first set carries reason MIGRATED_HISTORY).
/// </summary>
internal sealed class ImportFinancialHistoryHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, ClaimProtection protection, IOptions<ClaimsOptions> options, IOptions<ClaimFinancialsOptions> financials)
    : ICommandHandler<ImportFinancialHistory, ImportFinancialHistoryResponse>
{
    public const string ReasonCode = "MIGRATED_HISTORY";
    private const string FunctionalCode = "EUR";

    private sealed record HistoryLine(int ExposureSequence, string CostType, string CostCategory, Currency Currency, decimal Amount, decimal Rate, BusinessDate PostedOn);

    public async Task<Result<ImportFinancialHistoryResponse>> HandleAsync(ImportFinancialHistory command, CancellationToken cancellationToken)
    {
        if (command.Request.LegacyClaim is not { ValueKind: JsonValueKind.Object } payload)
        {
            return ImportJson.Invalid("legacyClaim is required.");
        }

        var number = ImportJson.Text(payload, "legacyNumber");
        if (string.IsNullOrWhiteSpace(number) || !payload.TryGetProperty("lines", out var linesElement) || linesElement.ValueKind != JsonValueKind.Array || linesElement.GetArrayLength() == 0)
        {
            return ImportJson.Invalid("legacyClaim needs legacyNumber and a non-empty lines array.");
        }

        var entity = protection.Current(context);
        var import = await db.MigrationImports.AsNoTracking().SingleOrDefaultAsync(m => m.LegalEntityId == entity && m.LegacyNumber == number, cancellationToken).ConfigureAwait(false);
        if (import is null)
        {
            return ImportJson.Invalid($"Legacy claim {number} has not been imported.");
        }

        var claim = await db.Claims.SingleAsync(c => c.ClaimId == import.ClaimId, cancellationToken).ConfigureAwait(false);
        if (await db.FinancialTransactions.AnyAsync(t => t.ClaimId == claim.ClaimId && t.ReasonCode == ReasonCode, cancellationToken).ConfigureAwait(false))
        {
            return new ImportFinancialHistoryResponse { Claim = ImportJson.Of(new { claimId = claim.ClaimId.Value, legacyNumber = number, posted = 0, skippedExisting = true }) };
        }

        var now = clock.Now;
        var settings = options.Value;
        var today = settings.DateOf(now);
        var parsed = new List<HistoryLine>();
        var index = 0;
        foreach (var item in linesElement.EnumerateArray())
        {
            var (line, error) = Parse(item, financials.Value, today);
            if (error is not null)
            {
                return ImportJson.Invalid($"lines[{index}]: {error}");
            }

            parsed.Add(line!);
            index++;
        }

        // A line's open reserve never falls below zero while the history is replayed in date order (REQ-CLM-095).
        var ordered = parsed.Select((l, i) => (Line: l, Index: i)).OrderBy(x => x.Line.PostedOn).ThenBy(x => x.Index).Select(x => x.Line).ToList();
        foreach (var group in ordered.GroupBy(l => (l.ExposureSequence, l.CostType, l.CostCategory, l.Currency.Code)))
        {
            var running = 0m;
            foreach (var line in group)
            {
                running += line.Amount;
                if (running < 0m)
                {
                    return ImportJson.Invalid($"The reserve of {group.Key.CostType}/{group.Key.CostCategory} {group.Key.Code} falls below zero.");
                }
            }
        }

        var exposures = await db.Exposures.Where(e => e.ClaimId == claim.ClaimId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var absent = ordered.Select(l => l.ExposureSequence).Distinct().Where(s => exposures.All(e => e.Sequence != s)).ToList();
        if (absent.Count > 0)
        {
            return ImportJson.Invalid($"The claim has no exposure {absent[0]}.");
        }

        var actor = context.Actor.ToString();
        var set = new TransactionSetRow
        {
            SetId = ClaimTransactionSetId.New(), ClaimId = claim.ClaimId, Status = Codes.Of(SetStatus.Draft), LegalEntityId = claim.LegalEntityId,
            Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = actor, UpdatedAt = now,
        };
        var existingLines = await db.ReserveLines.Where(l => l.ClaimId == claim.ClaimId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lines = new Dictionary<LineKey, ReserveLineRow>();
        var transactions = new List<FinancialTransactionRow>();
        var functionalUnits = Currency.FromCode(FunctionalCode).MinorUnits;
        foreach (var line in ordered)
        {
            var exposure = exposures.First(e => e.Sequence == line.ExposureSequence);
            var key = new LineKey(exposure.ExposureId, line.CostType, line.CostCategory, line.Currency.Code);
            if (!lines.TryGetValue(key, out var reserveLine))
            {
                reserveLine = existingLines.FirstOrDefault(l => SetLifecycle.Key(l) == key);
                if (reserveLine is null)
                {
                    reserveLine = new ReserveLineRow
                    {
                        ReserveLineId = ReserveLineId.New(), ClaimId = claim.ClaimId, ExposureId = key.ExposureId, CostType = key.CostType, CostCategory = key.CostCategory,
                        Currency = key.Currency, LegalEntityId = entity, Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = actor, UpdatedAt = now,
                    };
                    db.ReserveLines.Add(reserveLine);
                }

                lines[key] = reserveLine;
            }

            var functional = Math.Round(line.Amount * line.Rate, functionalUnits, MidpointRounding.AwayFromZero);
            claim.LastTransactionSequence++;
            transactions.Add(new FinancialTransactionRow
            {
                TxnId = Guid.CreateVersion7(), SetId = set.SetId, ClaimId = claim.ClaimId, ReserveLineId = reserveLine.ReserveLineId, ExposureId = exposure.ExposureId,
                Sequence = claim.LastTransactionSequence, TxnNumber = $"{claim.ClaimNumber.Value}-T{claim.LastTransactionSequence:D4}", Kind = Codes.Of(TransactionKind.Reserve),
                Amount = line.Amount, Currency = line.Currency.Code, FunctionalAmount = functional, FunctionalCurrency = FunctionalCode, GroupAmount = functional, GroupCurrency = FunctionalCode,
                ReasonCode = ReasonCode, Proposed = false, TransactionDate = line.PostedOn, LegalEntityId = entity, Jurisdiction = claim.Jurisdiction, CreatedAt = now, CreatedBy = actor,
            });
        }

        var content = new SetContent(set, transactions, lines.Values.ToDictionary(l => l.ReserveLineId), []);
        set.ContentHash = SetHashing.Content(set.SetId, claim.ClaimId, content.Canonical()).Value;
        set.BasisHash = SetLifecycle.Basis(claim, Array.Empty<(ExposureId, string)>(), Array.Empty<(LineKey, LineAmounts)>()).Value;
        db.TransactionSets.Add(set);
        db.FinancialTransactions.AddRange(transactions);
        claim.UpdatedAt = now;

        // The header seals its lines to this database transaction while it is a Draft (D-ARC-34); then it is approved
        // (no four-eyes: the batch itself was approved in MIG).
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        set.Status = Codes.Of(SetStatus.Approved);
        set.Submitter = actor;
        set.SubmittedAt = now;
        set.ApprovedAt = now;
        set.DecidedAt = now;
        set.Approver = actor;
        set.FourEyes = false;
        set.UpdatedAt = now;
        set.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var open = transactions.GroupBy(t => t.Currency).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount).ToString(CultureInfo.InvariantCulture));
        return new ImportFinancialHistoryResponse
        {
            Claim = ImportJson.Of(new { claimId = claim.ClaimId.Value, legacyNumber = number, setId = set.SetId.Value, posted = transactions.Count, openReserveByCurrency = open }),
        };
    }

    private static (HistoryLine? Line, string? Error) Parse(JsonElement item, ClaimFinancialsOptions settings, BusinessDate today)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return (null, "must be an object.");
        }

        var costType = ImportJson.Text(item, "costType") ?? string.Empty;
        var category = ImportJson.Text(item, "costCategory") ?? string.Empty;
        if (!Enum.GetValues<CostType>().Any(c => Codes.Of(c) == costType) || !settings.Allows(costType, category))
        {
            return (null, $"{costType}/{category} is not a configured cost type and category.");
        }

        if (ImportJson.Text(item, "kind") is { } kind && !string.Equals(kind, "RESERVE", StringComparison.Ordinal))
        {
            // TODO(hardening): PAYMENT history (needs payee, account and disbursement evidence) and recovery history.
            return (null, "only RESERVE movements can be imported; payment history is not supported yet.");
        }

        if (!item.TryGetProperty("amount", out var amountElement) || !amountElement.TryGetDecimal(out var amount) || amount == 0m)
        {
            return (null, "amount must be a non-zero number.");
        }

        if (!Currency.TryFromCode(ImportJson.Text(item, "currency") ?? FunctionalCode, out var currency) || Math.Round(amount, currency.MinorUnits) != amount)
        {
            return (null, "currency is unknown or amount has more decimals than the currency allows.");
        }

        var rate = 1m;
        if (item.TryGetProperty("legacyRate", out var rateElement))
        {
            if (!rateElement.TryGetDecimal(out rate) || rate <= 0m)
            {
                return (null, "legacyRate must be a positive number (legacy rate to EUR).");
            }
        }
        else if (currency.Code != FunctionalCode)
        {
            return (null, $"legacyRate is required for {currency.Code} (legacy rate to {FunctionalCode}).");
        }

        var sequence = item.TryGetProperty("exposureSequence", out var sequenceElement) && sequenceElement.TryGetInt32(out var s) ? s : 1;
        if (!DateOnly.TryParseExact(ImportJson.Text(item, "postedOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var posted) || new BusinessDate(posted) > today)
        {
            return (null, "postedOn must be a date (yyyy-MM-dd) not in the future.");
        }

        return (new HistoryLine(sequence, costType, category, currency, amount, rate, new BusinessDate(posted)), null);
    }
}

/// <summary>
/// Currency changeover plan (later markets): previews the open reserve of every claim line in the old currency converted at the plan's
/// rate. Nothing is posted. TODO(hardening): the apply step (paired ledger lines per claim, set approval) once a changeover market exists.
/// </summary>
internal sealed class ImportConvertCurrencyHandler(ClaimsDbContext db, RequestContext context, ClaimProtection protection)
    : ICommandHandler<ImportConvertCurrency, ImportConvertCurrencyResponse>
{
    public async Task<Result<ImportConvertCurrencyResponse>> HandleAsync(ImportConvertCurrency command, CancellationToken cancellationToken)
    {
        if (command.Request.Plan is not { ValueKind: JsonValueKind.Object } plan
            || !Currency.TryFromCode(ImportJson.Text(plan, "fromCurrency"), out var from) || !Currency.TryFromCode(ImportJson.Text(plan, "toCurrency"), out var to) || from == to
            || !plan.TryGetProperty("rate", out var rateElement) || !rateElement.TryGetDecimal(out var rate) || rate <= 0m)
        {
            return ImportJson.Invalid("plan needs fromCurrency, a different toCurrency and a positive rate.");
        }

        var entity = protection.Current(context);
        var rows = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.LegalEntityId == entity && t.Currency == from.Code && (t.Kind == "RESERVE" || t.Kind == "PAYMENT"))
            .Join(db.TransactionSets.Where(s => s.Status == "APPROVED" || s.Status == "POSTED"), t => t.SetId, s => s.SetId, (t, _) => t)
            .Select(t => new { t.ClaimId, t.ReserveLineId, t.Kind, t.Amount }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lines = rows.GroupBy(r => (r.ClaimId, r.ReserveLineId))
            .Select(g => new { claimId = g.Key.ClaimId.Value, reserveLineId = g.Key.ReserveLineId.Value, open = g.Sum(r => r.Kind == "RESERVE" ? r.Amount : -r.Amount) })
            .Where(l => l.open != 0m)
            .Select(l => new { l.claimId, l.reserveLineId, openBefore = l.open, openAfter = Math.Round(l.open * rate, to.MinorUnits, MidpointRounding.AwayFromZero) })
            .ToList();
        return new ImportConvertCurrencyResponse
        {
            ConversionResult = ImportJson.Of(new
            {
                fromCurrency = from.Code, toCurrency = to.Code, rate, applied = false, lineCount = lines.Count,
                totalBefore = lines.Sum(l => l.openBefore), totalAfter = lines.Sum(l => l.openAfter), lines,
            }),
        };
    }
}

internal sealed class ImportReverseHandler(IEnumerable<IMigrationReverser> reversers, RequestContext context)
    : ICommandHandler<ImportReverse, ImportReverseResponse>
{
    public async Task<Result<ImportReverseResponse>> HandleAsync(ImportReverse command, CancellationToken cancellationToken)
    {
        if (command.Request.BatchId is not { } batchId || batchId == Guid.Empty)
        {
            return ImportJson.Invalid("batchId is required.");
        }

        var reverser = reversers.OfType<ClaimMigrationReverser>().Single();

        // Check first: a batch with a claim that moved on is refused whole, nothing is removed (D-SL6-05).
        var check = await reverser.ReverseAsync(batchId, true, cancellationToken).ConfigureAwait(false);
        if (check.Any(o => !o.Reversed))
        {
            var blocked = string.Join("; ", check.Where(o => !o.Reversed).Select(o => $"{o.CoreId}: {o.Message}"));
            return DomainError.Of(ModuleCode.CLM, "REVERSE-LIVE-ACTIVITY", $"Batch {batchId} cannot be reversed: {blocked}");
        }

        var outcomes = context.DryRun ? check : await reverser.ReverseAsync(batchId, false, cancellationToken).ConfigureAwait(false);
        if (outcomes.Any(o => !o.Reversed))
        {
            return DomainError.Of(ModuleCode.CLM, "REVERSE-LIVE-ACTIVITY", $"Batch {batchId}: activity appeared on a claim during the reversal.");
        }

        return new ImportReverseResponse
        {
            ReversalResult = ImportJson.Of(new { batchId, dryRun = context.DryRun, reversed = outcomes.Count, claims = outcomes.Select(o => o.CoreId) }),
        };
    }
}
