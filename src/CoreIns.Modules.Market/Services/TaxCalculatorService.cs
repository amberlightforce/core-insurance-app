using System.Globalization;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// SPI 4 <see cref="ITaxCalculator"/> as bound by MKT (PRD-17 section 9.4.4): <c>treatment</c> (D2, REQ-MKT-330/331/332)
/// over the treatment rule rows of the country layer. Pure and fail closed: no rule row means <c>RULE_MISSING</c> and the core
/// holds no default. Rows are pack data with a legal status; the result is <c>provisional</c> when that status is not Settled,
/// and Production refuses a non-Settled row (D-REG-02, D-SLC-09) through the same gate as the configuration resolver.
/// <c>calculate</c> (SL3-MKT-CALCULATE) prices IPT from the configured rate rows.
/// </summary>
internal sealed class MarketTaxCalculator(ConfigurationEngine engine) : ITaxCalculator
{
    public ValueTask<TaxCalculationResult> CalculateAsync(TaxCalculationRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Calculate(request));

    /// <summary>
    /// <c>calculate</c> (REQ-MKT-332, PRD-17 7.5): IPT = base x <c>tax.ipt.rate.&lt;taxClass&gt;</c> at the tax point, exact multiply (refused on
    /// precision loss), rounded by the configured tax-line rounding rule (<c>cur.rounding.tax.&lt;class&gt;</c>, else <c>cur.rounding.tax.line</c>,
    /// else the default; the order of <c>mkt.Rounding.apply</c>). Fail closed: only a Premium charge is taxed, every other kind and any missing row
    /// is <c>RULE_MISSING</c>. The line's legal status is the weaker of the rate and the rounding rule, and Production refuses anything not exactly
    /// Settled (PITFALLS 36). The sign follows the treatment action: a credit base only for ReduceProRata or ReverseAsVoid.
    /// </summary>
    internal TaxCalculationResult Calculate(TaxCalculationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChargeLines is null || request.ChargeLines.Count == 0)
        {
            throw Validation("CHARGE_LINES_REQUIRED", nameof(request.ChargeLines));
        }

        if (string.IsNullOrWhiteSpace(request.RiskJurisdiction))
        {
            throw Validation("JURISDICTION_REQUIRED", nameof(request.RiskJurisdiction));
        }

        if (request.TaxPointDate == default)
        {
            throw Validation("TAX_POINT_DATE_REQUIRED", nameof(request.TaxPointDate));
        }

        var action = request.TreatmentAction ?? TreatmentAction.Apply;
        if (action is not (TreatmentAction.Apply or TreatmentAction.ReduceProRata or TreatmentAction.ReverseAsVoid))
        {
            throw Validation("TREATMENT_ACTION_NOT_CALCULABLE", nameof(request.TreatmentAction));
        }

        var lines = new List<TaxLine>(request.ChargeLines.Count);
        foreach (var line in request.ChargeLines)
        {
            lines.Add(CalculateLine(request, action, line));
        }

        return new TaxCalculationResult(lines, []);
    }

    private TaxLine CalculateLine(TaxCalculationRequest request, TreatmentAction action, TaxChargeLine line)
    {
        if (string.IsNullOrWhiteSpace(line.TaxClass))
        {
            throw Validation("TAX_CLASS_REQUIRED", nameof(line.TaxClass));
        }

        if (string.IsNullOrWhiteSpace(line.Element) || string.IsNullOrWhiteSpace(line.ChargeType))
        {
            throw Validation("CHARGE_LINE_INCOMPLETE", nameof(line.Element));
        }

        if (line.ChargeCategory is not { } category || !Enum.IsDefined(category))
        {
            throw Validation("CHARGE_CATEGORY_REQUIRED", nameof(line.ChargeCategory));
        }

        if (line.PeriodEnd < line.PeriodStart)
        {
            throw Validation("PERIOD_INVALID", nameof(line.PeriodEnd));
        }

        if (!Currency.TryFromCode(line.PremiumAmount.Currency, out var currency))
        {
            throw Validation("CURRENCY_UNKNOWN", nameof(line.PremiumAmount));
        }

        var at = new BusinessDate(request.TaxPointDate);

        // Allow-list: IPT is priced on a Premium charge only. Levy, stamp, fee and tax lines have no settled rate row (D-REG-06a, D-SL3-06).
        if (category != ChargeLineCategory.Premium)
        {
            throw RuleMissing($"No rate rule for a {category} charge in {request.RiskJurisdiction}; only Premium is taxed with IPT and the core holds no default (fail closed).");
        }

        // The jurisdiction's currency roles (cur.transaction, cur.functional): a line in any other currency is refused.
        var roles = CurrencyRoleKeys
            .Select(k => engine.Catalogue.Find(k, request.RiskJurisdiction, at))
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .ToList();
        if (roles.Count == 0)
        {
            throw RuleMissing($"No currency role (cur.transaction, cur.functional) for {request.RiskJurisdiction}; fail closed.");
        }

        if (!roles.Contains(line.PremiumAmount.Currency, StringComparer.Ordinal))
        {
            throw Validation("CURRENCY_MISMATCH", nameof(line.PremiumAmount));
        }

        var taxClass = line.TaxClass.Trim().ToLowerInvariant();
        var key = IptRatePrefix + taxClass;
        var entry = ConfigKeys.Find(key) is null ? null : engine.Catalogue.Find(key, request.RiskJurisdiction, at);
        if (entry is null)
        {
            throw RuleMissing($"No IPT rate {key} in {request.RiskJurisdiction} on {request.TaxPointDate:yyyy-MM-dd}; the core holds no default (fail closed).");
        }

        // Rounding rule, same precedence as mkt.Rounding.apply (BR-MKT-027): tax-class rule, tax.line purpose rule, currency default.
        ConfigEntry? roundingEntry = null;
        foreach (var candidate in new[] { ConfigKeys.RoundingTaxPrefix + taxClass, ConfigKeys.RoundingPrefix + "tax.line", ConfigKeys.RoundingDefault })
        {
            if (ConfigKeys.Find(candidate) is not null && engine.Catalogue.Find(candidate, request.RiskJurisdiction, at) is { } found)
            {
                roundingEntry = found;
                break;
            }
        }

        if (roundingEntry is null)
        {
            throw RuleMissing($"No tax-line rounding rule in {request.RiskJurisdiction}, not even the currency default; fail closed.");
        }

        // The weaker of the rate and the rounding rule is the status of the line. A NotRegulatory rounding rule (a core default) does not weaken it.
        var weakest = entry.LegalStatus != LegalStatus.Settled ? entry
            : roundingEntry.LegalStatus is LegalStatus.Settled or LegalStatus.NotRegulatory ? entry
            : roundingEntry;
        if (engine.EnforceSettled && weakest.LegalStatus != LegalStatus.Settled)
        {
            throw new DomainException(DomainError.Of(
                ModuleCode.MKT, "CFG-NOT-SETTLED",
                $"Production refuses values that are not Settled (D-REG-02, REQ-MKT-343): {weakest.Key} ({weakest.LegalStatus})."));
        }

        var amount = line.PremiumAmount.Amount;
        var reducing = action is TreatmentAction.ReduceProRata or TreatmentAction.ReverseAsVoid;
        if (amount < 0m && !reducing)
        {
            throw Validation("CREDIT_BASE_NOT_REDUCING", nameof(line.PremiumAmount));
        }

        if (amount > 0m && reducing)
        {
            throw Validation("DEBIT_BASE_UNDER_REDUCING_TREATMENT", nameof(line.PremiumAmount));
        }

        if (!decimal.TryParse(entry.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) || rate < 0m || rate > 1m)
        {
            throw RuleMissing($"{entry.Key} does not hold a rate between 0 and 1; refused (fail closed).");
        }

        var rule = RoundingRule.Parse(roundingEntry.ToJsonElement());
        decimal tax;
        try
        {
            // The exact multiply of the quote-time path (Money.Multiply): precision loss is refused, never silently rounded.
            tax = rule.Apply(ExactDecimal.Multiply(amount, rate), rule.Scale ?? currency.MinorUnits);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidOperationException or ArgumentException or PrecisionLossException)
        {
            throw Validation("AMOUNT_NOT_REPRESENTABLE", nameof(line.PremiumAmount));
        }

        return new TaxLine
        {
            Element = line.Element,
            ChargeType = "IPT",
            Category = TaxCategory.Tax,
            TaxClass = line.TaxClass,
            Base = line.PremiumAmount,
            Rate = rate,
            Amount = new SpiMoney(tax, line.PremiumAmount.Currency),
            RoundingRuleId = rule.IdFor(roundingEntry.Key).ToString(),
            RuleId = entry.Key,
            RuleVersion = entry.VersionId.ToString(),
            LegalSourceRef = $"{entry.SourceRef}; rounding {roundingEntry.Key} ({roundingEntry.LegalStatus})",
            LegalStatus = weakest.LegalStatus,
            ConfigurationHash = engine.Catalogue.Hash.Hash.ToString(),
        };
    }

    private const string IptRatePrefix = "tax.ipt.rate.";

    private static readonly string[] CurrencyRoleKeys = ["cur.transaction", "cur.functional"];

    private static SpiException RuleMissing(string message) =>
        new(new SpiError(SpiErrorCategory.RuleMissing, "RULE_MISSING"), message);

    public ValueTask<TaxTreatmentResult> TreatmentAsync(TaxTreatmentRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Treatment(request));

    internal TaxTreatmentResult Treatment(TaxTreatmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.TransactionKind) || !Enum.IsDefined(request.Category))
        {
            throw Validation("TRANSACTION_KIND_UNKNOWN", nameof(request.TransactionKind));
        }

        if (string.IsNullOrWhiteSpace(request.ChargeType))
        {
            throw Validation("CHARGE_TYPE_REQUIRED", nameof(request.ChargeType));
        }

        var needsSource = request.TransactionKind is TaxTransactionKind.Cancellation or TaxTransactionKind.Void;
        string? source = null;
        if (needsSource)
        {
            if (string.IsNullOrWhiteSpace(request.CancellationSource))
            {
                throw Validation("CANCELLATION_SOURCE_REQUIRED", nameof(request.CancellationSource));
            }

            if (!CancellationSources.All.Contains(request.CancellationSource))
            {
                throw Validation("CANCELLATION_SOURCE_UNKNOWN", nameof(request.CancellationSource));
            }

            source = request.CancellationSource;
        }

        if (request.TransactionKind == TaxTransactionKind.DistanceWithdrawalVoid
            && !string.IsNullOrWhiteSpace(request.CancellationSource)
            && request.CancellationSource != TaxTreatmentRules.DistanceWithdrawal)
        {
            throw Validation("CANCELLATION_SOURCE_MISMATCH", nameof(request.CancellationSource));
        }

        var key = TaxTreatmentRules.Key(request.Category, request.TransactionKind, source);
        var entry = engine.Catalogue.Find(key, request.RiskJurisdiction, new BusinessDate(request.TaxPointDate));
        if (entry is null)
        {
            throw new SpiException(
                new SpiError(SpiErrorCategory.RuleMissing, "RULE_MISSING"),
                $"No treatment rule for {key} in {request.RiskJurisdiction} on {request.TaxPointDate:yyyy-MM-dd}; the core holds no default (fail closed).");
        }

        if (engine.EnforceSettled && entry.LegalStatus != LegalStatus.Settled)
        {
            throw new DomainException(DomainError.Of(
                ModuleCode.MKT, "CFG-NOT-SETTLED",
                $"Production refuses values that are not Settled (D-REG-02, REQ-MKT-343): {entry.Key} ({entry.LegalStatus})."));
        }

        var row = TaxTreatmentRules.Parse(entry.Key, entry.Value);
        var (credit, liability, document) = TaxTreatmentRules.Details(row.Action);
        return new TaxTreatmentResult
        {
            ChargeType = request.ChargeType,
            Action = row.Action,
            CustomerCredit = credit,
            AuthorityLiability = liability,
            FiscalDocument = document,
            RuleId = row.RuleId,
            RuleVersion = row.RuleVersion,
            LegalStatus = entry.LegalStatus == LegalStatus.Settled ? TreatmentLegalStatus.Settled : TreatmentLegalStatus.Pending,
            LegalSourceRef = entry.SourceRef,
            ConfigurationHash = engine.Catalogue.Hash.Hash.ToString(),
        };
    }

    private static SpiException Validation(string code, string field) =>
        new(new SpiError(SpiErrorCategory.Validation, code, field), $"MKT-ERR-SPI-VALIDATION: {code} ({field}).");
}
