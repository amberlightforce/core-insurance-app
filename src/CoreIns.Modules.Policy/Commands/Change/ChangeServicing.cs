using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Commands.Change;

/// <summary>
/// Port: the tax and levy lines on the premium deltas of one transaction (RAT servicing tax lines, D-SL3-05: a debit applies the
/// tax, a credit keeps it not reduced, both from MKT's <c>TaxCalculator.treatment</c> with rule id, version and legal status).
/// POL never computes tax. The adapter over <c>rat.Proration</c> and MKT treatment arrives with SL3-RAT-PRORATE and
/// SL3-MKT-TREATMENT; until then the registered adapter fails closed.
/// </summary>
internal interface IServicingTax
{
    /// <summary>The tax lines for the premium deltas, or a typed refusal.</summary>
    Task<Result<IReadOnlyList<PricedTaxLine>>> LinesAsync(ServicingTaxRequest request, CancellationToken cancellationToken);
}

/// <summary>The registered adapter until RAT proration and MKT treatment are on main: a change that needs tax fails closed (PITFALLS 10).</summary>
internal sealed class UnavailableServicingTax : IServicingTax
{
    public Task<Result<IReadOnlyList<PricedTaxLine>>> LinesAsync(ServicingTaxRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<IReadOnlyList<PricedTaxLine>>>(DomainError.Of(
            ModuleCode.POL, "DEPENDENCY-UNAVAILABLE", "Servicing tax lines (rat.Proration + mkt TaxCalculator.treatment) are not wired in this deployment yet."));
}

/// <summary>
/// The registered proration until RAT's real one is bound (SL3-POL-WIRING): fails closed. <c>ReferenceProration</c> is test-only.
/// </summary>
internal sealed class UnavailableProration : IProration
{
    public ProrationFraction Fraction(DayCountConvention convention, int days, int termDays) =>
        throw new DomainException(DomainError.Of(ModuleCode.POL, "DEPENDENCY-UNAVAILABLE", "rat.Proration is not bound in this deployment yet; servicing fails closed."));
}

/// <summary>
/// The engine for one term: the day-count convention from the pinned product artefact (fail closed on an unknown code) and MKT's
/// premium rounding rule, resolved once by one probe of <c>mkt.Rounding.apply</c> (purpose <c>charge.line</c>, the same rule the
/// issuance used) so that the synchronous engine rounds exactly as MKT does.
/// </summary>
internal sealed class ChangeEngineFactory(
    Dependency<IProductArtifactService> artifacts,
    Dependency<IMarketRoundingService> rounding,
    IProration proration,
    IServicingTax servicingTax,
    RequestContext context)
{
    public async Task<Result<ChangeEngine>> CreateAsync(PolicyTermRow term, Instant validAt, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(term);
        var convention = await ConventionAsync(term, cancellationToken).ConfigureAwait(false);
        if (convention.IsFailure)
        {
            return convention.Error!;
        }

        var rule = await RoundingAsync(term, validAt, zone, cancellationToken).ConfigureAwait(false);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        (servicingTax as IBoundServicingTax)?.Bind(term.ArtefactHash);
        // The production proration (RAT behind POL's port) is told which term and pinned artefact it prorates for; test doubles are not bound.
        (proration as Cancellation.IBoundProration)?.Bind(
            term.ArtefactHash, ConfigurationHash.Parse(term.ConfigurationHash),
            new ServicingTerm(term.ValidFrom, term.ValidTo, Currency.FromCode(term.Currency.Trim()), convention.Value, zone));
        return new ChangeEngine(new ServicingEngine(proration, rule.Value), convention.Value, rule.Value);
    }

    private async Task<Result<DayCountConvention>> ConventionAsync(PolicyTermRow term, CancellationToken cancellationToken)
    {
        var artefact = await artifacts.Value.GetAsync(term.ArtefactHash, cancellationToken: cancellationToken).ConfigureAwait(false);
        var code = artefact.CanonicalJsonArtefact is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("dayCount", out var value) ? value.GetString() : null;
        // PFC spells ACT/365F with a slash; the engine's code is ACT_365F.
        return DayCountConventions.TryParse(code?.Replace('/', '_'), out var convention)
            ? convention
            : DomainError.Of(ModuleCode.POL, "VALIDATION", $"The product artefact carries no known day-count convention ({code ?? "absent"}); a change fails closed.");
    }

    private async Task<Result<PremiumRounding>> RoundingAsync(PolicyTermRow term, Instant validAt, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var currency = Currency.FromCode(term.Currency);
        RoundingApplyResponse probe;
        try
        {
            probe = await rounding.Value.ApplyAsync(
                new RoundingApplyRequest
                {
                    Amount = new Money(1.005m, currency), Currency = currency, Purpose = "charge.line",
                    Context = new RoundingApplyRequest.ContextDetail { LegalEntity = context.LegalEntity!.Value.Value, ValidAt = validAt.ToBusinessDate(zone) },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module != ModuleCode.POL)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", $"Rounding failed: {ex.Error.Code}.");
        }

        if (probe.Mode is null || probe.Scale is null || probe.Scale is < 0 or > 4)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", "MKT returned no usable premium rounding rule.");
        }

        var scale = probe.Scale.Value;
        return probe.Mode.Value switch
        {
            RoundingApplyResponse.ModeValue.HalfUp => new PremiumRounding(a => decimal.Round(a, scale, MidpointRounding.AwayFromZero)),
            RoundingApplyResponse.ModeValue.HalfEven => new PremiumRounding(a => decimal.Round(a, scale, MidpointRounding.ToEven)),
            RoundingApplyResponse.ModeValue.Down => new PremiumRounding(a => decimal.Round(a, scale, MidpointRounding.ToZero)),
            RoundingApplyResponse.ModeValue.Up => new PremiumRounding(a => decimal.Round(a, scale, a >= 0 ? MidpointRounding.ToPositiveInfinity : MidpointRounding.ToNegativeInfinity)),
            RoundingApplyResponse.ModeValue.Ceiling => new PremiumRounding(a => decimal.Round(a, scale, MidpointRounding.ToPositiveInfinity)),
            _ => new PremiumRounding(a => decimal.Round(a, scale, MidpointRounding.ToNegativeInfinity)),
        };
    }
}

/// <summary>The engine of a term with its day-count convention and MKT's premium rounding.</summary>
internal sealed record ChangeEngine(ServicingEngine Engine, DayCountConvention Convention, PremiumRounding Rounding);

/// <summary>A term's current version with the bound transactions that built it.</summary>
internal sealed record TermHistory(PolicyTermRow Term, IReadOnlyList<PolicyTransactionRow> Transactions)
{
    /// <summary>The latest effective time of a bound transaction (the in-sequence boundary, D-SL3-02).</summary>
    public Instant LatestBoundEffective => Transactions.Count == 0 ? Term.ValidFrom : Instant.Max(Term.ValidFrom, Transactions.Max(t => t.EffectiveAt));
}

/// <summary>Loads a term's history and rebuilds the engine state by replaying its transactions.</summary>
internal sealed class ChangeHistory(PolicyDbContext db)
{
    /// <summary>The term's current version and transactions; <paramref name="tracked"/> when the caller will supersede the version.</summary>
    public async Task<Result<TermHistory>> LoadAsync(LegalEntityId legalEntity, PolicyTermId termId, bool tracked, CancellationToken cancellationToken)
    {
        var terms = tracked ? db.Terms : db.Terms.AsNoTracking();
        var term = await terms.SingleOrDefaultAsync(t => t.TermId == termId && t.LegalEntityId == legalEntity && t.RecordedTo == null, cancellationToken).ConfigureAwait(false);
        if (term is null)
        {
            return JobSupport.NotFound("term");
        }

        var transactions = await db.Transactions.AsNoTracking().Where(t => t.TermId == termId && t.LegalEntityId == legalEntity)
            .OrderBy(t => t.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new TermHistory(term, transactions);
    }

    /// <summary>
    /// G1 (REQ-POL-104) and the term state: a cancelled cover refuses everything at or after it; only a Scheduled or InForce term
    /// can be changed; the effective time must lie inside the term.
    /// </summary>
    public static DomainError? GuardState(TermHistory history, Instant effectiveAt)
    {
        var term = history.Term;
        var state = Codes.Parse<PolicyTermState>(term.State);
        if (state == PolicyTermState.Cancelled || term.CancelledAt is not null)
        {
            return DomainError.Of(ModuleCode.POL, PolicyErrorNames.AfterCancellation, "The cover of this term has ended; a change after a bound cancellation is refused (REQ-POL-104).");
        }

        if (state is not (PolicyTermState.Scheduled or PolicyTermState.InForce))
        {
            return DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", $"A {state} term cannot be changed.");
        }

        return effectiveAt < term.ValidFrom || effectiveAt >= term.ValidTo
            ? DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION", "The effective time is outside the term.")
            : null;
    }

    /// <summary>The in-sequence rule (D-SL3-02): not earlier than the effective time of the term's latest bound transaction.</summary>
    public static DomainError? GuardSequence(TermHistory history, Instant effectiveAt) =>
        effectiveAt < history.LatestBoundEffective
            ? DomainError.Of(ModuleCode.POL, PolicyErrorNames.OutOfSequence, "The effective time is earlier than the term's latest bound transaction (D-SL3-02).")
            : null;

    /// <summary>Both guards, state first.</summary>
    public static DomainError? Guard(TermHistory history, Instant effectiveAt) => GuardState(history, effectiveAt) ?? GuardSequence(history, effectiveAt);

    /// <summary>
    /// Replays the term: the opening transaction's premium rates, then every Change intent in sequence order. Cross-checks that the
    /// replayed segments add up to the premium charge lines written for the term (P7); anything else fails closed.
    /// </summary>
    public async Task<Result<ServicingState>> ReplayAsync(
        TermHistory history, ServicingEngine engine, DayCountConvention convention, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var term = history.Term;
        if (history.Transactions.Count == 0)
        {
            return DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", "The term has no transaction.");
        }

        var currency = Currency.FromCode(term.Currency);
        var opener = history.Transactions[0];
        var lines = await db.ChargeLines.AsNoTracking()
            .Where(c => c.TransactionId == opener.TransactionId && c.ChargeCategory == ChargeCategories.Premium).ToListAsync(cancellationToken).ConfigureAwait(false);
        var correlation = new DeltaCorrelation(term.TermId.Value.ToString(), term.TermId.Value.ToString());
        var opened = engine.Apply(
            null,
            new NewTermIntent(
                new ServicingTerm(term.ValidFrom, term.ValidTo, currency, convention, zone),
                [.. lines.Select(l => new ChargeRate(l.ElementLocator, l.CoverageCode, l.ChargeType, l.ChargeCategory, l.AnnualRate))]),
            correlation);
        if (!opened.IsAccepted)
        {
            return Invariant($"The opening of the term does not replay: {opened.Message}");
        }

        var state = opened.State!;
        foreach (var transaction in history.Transactions.Skip(1))
        {
            if (Codes.Parse<PolicyTransactionKind>(transaction.Kind) != PolicyTransactionKind.Change)
            {
                return DomainError.Of(
                    ModuleCode.POL, "ILLEGAL-TRANSITION", $"The term history holds a {transaction.Kind} transaction that a change cannot build on yet.");
            }

            var rates = ChangeJson.Rates(transaction.Intent, out var effectiveAt);
            if (rates is null)
            {
                return Invariant($"The intent of change transaction {transaction.TransactionId.Value} cannot be replayed.");
            }

            var applied = engine.Apply(state, new ChangeIntent(effectiveAt, rates), correlation);
            if (!applied.IsAccepted)
            {
                return Invariant($"Change transaction {transaction.TransactionId.Value} does not replay: {applied.Message}");
            }

            state = applied.State!;
        }

        var written = await db.ChargeLines.AsNoTracking().Where(c => c.TermId == term.TermId && c.ChargeCategory == ChargeCategories.Premium)
            .SumAsync(c => c.Amount, cancellationToken).ConfigureAwait(false);
        if (state.Segments.Sum(s => s.Amount) != written)
        {
            return Invariant("The replayed segments do not add up to the premium written for the term.");
        }

        return state;
    }

    private static DomainError Invariant(string message) => DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", message);
}

/// <summary>The priced change: the engine's segments and NET premium deltas, the tax lines on them and the preview.</summary>
internal sealed record ChangePricing(
    ServicingState Before,
    ServicingState After,
    IReadOnlyList<ServicingDelta> Deltas,
    IReadOnlyList<PricedTaxLine> TaxLines,
    IReadOnlyList<ChargeRate> NewRates,
    Money Premium,
    Money Taxes)
{
    public Money Total => Premium + Taxes;

    /// <summary>
    /// The contract <c>servicingPreview</c> (REQ-POL-192): annual premium before and after, the prorated lines per element × charge
    /// type, the tax lines with their treatment, the signed changes and what is due either way. Built by the code the bind runs.
    /// </summary>
    public ServicingPreview ToPreview(PremiumRounding rounding, ConfigurationHash? configuration)
    {
        var term = After.Term;
        var currency = term.Currency;
        var termDays = term.Days;
        var rates = NewRates.ToDictionary(r => r.Key);
        var before = Before.Segments.GroupBy(s => s.Key).Select(g => g.OrderBy(s => s.From).Last()).Sum(s => rounding(s.Rate.AnnualRate));
        var after = NewRates.Sum(r => rounding(r.AnnualRate));
        var prorated = Deltas.Select(d => new ServicingProratedLine
        {
            ElementLocator = d.Key.ElementLocator, CoverageCode = d.Key.CoverageCode, ChargeType = d.Key.ChargeType, ChargeCategory = d.ChargeCategory,
            Period = DateRange.Of(new BusinessDate(d.DateFrom), new BusinessDate(d.DateTo)), Days = d.Days, TermDays = termDays,
            Fraction = decimal.Round((decimal)d.FractionNumerator / d.FractionDenominator, 12, MidpointRounding.ToZero),
            AnnualAmount = new Money(rates.TryGetValue(d.Key, out var rate) ? rounding(rate.AnnualRate) : 0m, currency), Amount = new Money(d.Amount, currency),
        }).ToList();
        var taxLines = TaxLines.Select(t => new Contracts.Api.ServicingTaxLine
        {
            ElementLocator = t.SourceKey.ElementLocator, ChargeType = t.ChargeType, ChargeCategory = t.ChargeCategory, Amount = new Money(t.Amount, currency),
            TreatmentAction = t.Action, RuleId = t.RuleId, RuleVersion = t.RuleVersion, LegalStatus = t.LegalStatus, Provisional = t.Provisional,
        }).ToList();
        var total = Total;
        return new ServicingPreview
        {
            AnnualBefore = new Money(before, currency), AnnualAfter = new Money(after, currency), ProratedLines = prorated, TaxLines = taxLines,
            PremiumChange = Premium, TaxChange = Taxes, TotalChange = total,
            RefundDue = new Money(total.Amount < 0m ? -total.Amount : 0m, currency), AdditionalDue = new Money(total.Amount > 0m ? total.Amount : 0m, currency),
            TransactionKind = total.Amount < 0m ? TransactionKindCode.EndorsementCredit : TransactionKindCode.EndorsementDebit,
            ConfigurationHash = configuration, Provisional = TaxLines.Any(t => t.Provisional),
        };
    }
}

/// <summary>Prices a change with the engine and the tax port: the one code path of preview, quote and bind (POL P8).</summary>
internal sealed class ChangePricer(IServicingTax tax, RequestContext context)
{
    public async Task<Result<ChangePricing>> PriceAsync(
        ServicingEngine engine, ServicingState before, IReadOnlyList<ChargeRate> newRates, Instant effectiveAt, string setId, string jurisdiction,
        TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var applied = engine.Apply(before, new ChangeIntent(effectiveAt, newRates), new DeltaCorrelation(context.CorrelationId.ToString(), setId));
        if (!applied.IsAccepted)
        {
            return Refusal(applied);
        }

        var currency = before.Term.Currency;
        IReadOnlyList<PricedTaxLine> taxLines = [];
        if (applied.Deltas.Count > 0)
        {
            var taxed = await tax.LinesAsync(
                new ServicingTaxRequest(context.LegalEntity!.Value.Value, jurisdiction, effectiveAt.ToBusinessDate(zone), currency, applied.Deltas), cancellationToken)
                .ConfigureAwait(false);
            if (taxed.IsFailure)
            {
                return taxed.Error!;
            }

            taxLines = taxed.Value;
            var checkedTax = CheckTax(applied.Deltas, taxLines);
            if (checkedTax is not null)
            {
                return checkedTax;
            }
        }

        var premium = Money.Sum(applied.Deltas.Select(d => new Money(d.Amount, currency)), currency);
        var taxes = Money.Sum(taxLines.Select(t => new Money(t.Amount, currency)), currency);
        return new ChangePricing(before, applied.State!, applied.Deltas, taxLines, newRates, premium, taxes);
    }

    /// <summary>
    /// Fail closed on the tax port (PITFALLS 10): every premium delta has exactly one tax line, a debit applies the tax, a credit keeps it
    /// not reduced with 0.00 (D-SL3-05), and amounts are in minor units.
    /// </summary>
    private static DomainError? CheckTax(IReadOnlyList<ServicingDelta> deltas, IReadOnlyList<PricedTaxLine> lines)
    {
        foreach (var delta in deltas)
        {
            var own = lines.Where(l => l.SourceKey == delta.Key).ToList();
            if (own.Count != 1)
            {
                return DomainError.Of(ModuleCode.POL, "RATING", $"{delta.Key.ChargeType} has {own.Count} tax lines; a servicing delta needs exactly one.");
            }

            var line = own[0];
            var debit = delta.Amount > 0m;
            var wrongAction = debit ? line.Action != TreatmentActionCode.Apply : line.Action != TreatmentActionCode.KeepNotReduced || line.Amount != 0m;
            if (wrongAction || decimal.Round(line.Amount, 2) != line.Amount)
            {
                return DomainError.Of(
                    ModuleCode.POL, "RATING",
                    $"The tax treatment of {delta.Key.ChargeType} ({line.Action}, {line.Amount}) does not fit a {(debit ? "debit" : "credit")} (D-SL3-05).");
            }
        }

        return lines.Any(l => deltas.All(d => d.Key != l.SourceKey))
            ? DomainError.Of(ModuleCode.POL, "RATING", "A tax line does not belong to a premium delta of this change.")
            : null;
    }

    private static DomainError Refusal(ServicingResult result) => result.Refusal switch
    {
        ServicingRefusal.OutOfSequence => DomainError.Of(ModuleCode.POL, PolicyErrorNames.OutOfSequence, result.Message ?? "Out of sequence."),
        ServicingRefusal.AfterCancellation => DomainError.Of(ModuleCode.POL, PolicyErrorNames.AfterCancellation, result.Message ?? "The cover has ended."),
        ServicingRefusal.EffectiveOutsideTerm => DomainError.Of(ModuleCode.POL, PolicyErrorNames.EffdateLimit, result.Message ?? "Outside the term."),
        _ => DomainError.Of(ModuleCode.POL, "SEGMENT-INVARIANT", result.Message ?? "The change cannot be applied to the term's segments."),
    };
}

/// <summary>Compares the base risk tree with the edited one: the diff for the user, and what a mid-term change does not allow.</summary>
internal static class RiskDiffs
{
    public static RiskDiff Compute(RiskTree before, RiskTree after, IReadOnlyCollection<string> editableVehicleFields)
    {
        var entries = new List<DiffEntry>();
        var violations = new List<(string Field, string Message)>();
        var beforeVehicles = before.Vehicles.ToDictionary(v => v.Locator!, StringComparer.Ordinal);
        var afterVehicles = after.Vehicles.ToDictionary(v => v.Locator!, StringComparer.Ordinal);
        var removed = beforeVehicles.Keys.Where(k => !afterVehicles.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var added = afterVehicles.Keys.Where(k => !beforeVehicles.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var changed = new List<string>();

        foreach (var (locator, was) in beforeVehicles.Where(kv => afterVehicles.ContainsKey(kv.Key)))
        {
            var now = afterVehicles[locator];
            var touched = false;
            foreach (var (field, get) in VehicleFields)
            {
                var (b, a) = (get(was), get(now));
                if (string.Equals(b, a, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!editableVehicleFields.Contains(field, StringComparer.Ordinal))
                {
                    violations.Add(($"riskTree.vehicles[{locator}].{field}", $"The vehicle field {field} cannot be edited by a mid-term change."));
                    continue;
                }

                entries.Add(new DiffEntry("vehicle", locator, field, b, a));
                touched = true;
            }

            if (touched)
            {
                changed.Add(locator);
            }
        }

        if (added.Count != removed.Count || added.Count > 1)
        {
            violations.Add(("riskTree.vehicles", "A change edits the vehicle or replaces it with one other vehicle."));
        }

        var mapping = added.Count == 1 && removed.Count == 1 ? (From: removed[0], To: added[0]) : default;
        foreach (var locator in removed)
        {
            entries.Add(new DiffEntry("vehicle", locator, "vehicle", Describe(beforeVehicles[locator]), null));
        }

        foreach (var locator in added)
        {
            entries.Add(new DiffEntry("vehicle", locator, "vehicle", null, Describe(afterVehicles[locator])));
        }

        // Everything but the vehicle stays as it was; a replaced vehicle's locator maps to the new one (the cover and the driver follow it).
        string Map(string? locator) => locator is not null && locator == mapping.From ? mapping.To : locator ?? string.Empty;
        if (Canonical(before.Drivers.Select(d => d with { VehicleLocator = d.VehicleLocator is null ? null : Map(d.VehicleLocator) }).OrderBy(d => d.Locator, StringComparer.Ordinal))
            != Canonical(after.Drivers.OrderBy(d => d.Locator, StringComparer.Ordinal)))
        {
            violations.Add(("riskTree.drivers", "A mid-term change cannot edit the drivers."));
        }

        if (Canonical(before.Coverages.Where(c => c.Selected).Select(c => c with { ElementLocator = c.ElementLocator is null ? null : Map(c.ElementLocator) })
                .OrderBy(c => c.CoverageCode, StringComparer.Ordinal).ThenBy(c => c.ElementLocator, StringComparer.Ordinal))
            != Canonical(after.Coverages.Where(c => c.Selected).OrderBy(c => c.CoverageCode, StringComparer.Ordinal).ThenBy(c => c.ElementLocator, StringComparer.Ordinal)))
        {
            violations.Add(("riskTree.coverages", "A mid-term change cannot edit the cover; a replaced vehicle keeps the cover it had."));
        }

        if (Canonical(before.QuestionSets.OrderBy(q => q.QuestionSetCode, StringComparer.Ordinal)) != Canonical(after.QuestionSets.OrderBy(q => q.QuestionSetCode, StringComparer.Ordinal)))
        {
            violations.Add(("riskTree.questionSets", "A mid-term change cannot edit the answers."));
        }

        return new RiskDiff(entries, violations, added, removed, changed);
    }

    private static readonly (string Field, Func<Vehicle, string?> Get)[] VehicleFields =
    [
        ("plate", v => v.Plate),
        ("vin", v => v.Vin),
        ("make", v => v.Make),
        ("model", v => v.Model),
        ("firstRegistrationYear", v => v.FirstRegistrationYear?.ToString(CultureInfo.InvariantCulture)),
        ("engineCapacityCc", v => v.EngineCapacityCc?.ToString(CultureInfo.InvariantCulture)),
        ("use", v => v.Use),
        ("value", v => v.Value is { } m ? m.ToString() : null),
        ("fields", v => v.Fields?.GetRawText()),
    ];

    /// <summary>A vehicle for the diff without identification: make, model, year and engine (never the plate or the VIN).</summary>
    private static string Describe(Vehicle vehicle) =>
        string.Join(' ', new[] { vehicle.Make, vehicle.Model, vehicle.FirstRegistrationYear?.ToString(CultureInfo.InvariantCulture), vehicle.EngineCapacityCc is { } cc ? cc.ToString(CultureInfo.InvariantCulture) + "cc" : null }
            .Where(s => !string.IsNullOrEmpty(s)));

    private static string Canonical<T>(IEnumerable<T> items) => JobSupport.Json(items.ToList());
}
