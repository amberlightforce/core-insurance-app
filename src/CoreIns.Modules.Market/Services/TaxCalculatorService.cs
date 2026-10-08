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

    /// <summary>Transaction types whose negative (credit) base may carry a credit tax: only a reducing treatment (REDUCE_PRO_RATA, REVERSE_AS_VOID).</summary>
    private static readonly HashSet<string> ReducingTransactionTypes = new(StringComparer.OrdinalIgnoreCase) { "VOID", "REDUCE_PRO_RATA" };

    /// <summary>
    /// <c>calculate</c> (REQ-MKT-332, PRD-17 7.5): IPT = base x <c>tax.ipt.rate.&lt;taxClass&gt;</c> at the tax point, rounded half-up to the
    /// currency minor unit (the quote-time rule of the rating tax plan GR-IPT, so a servicing delta prices exactly like a quote). Fail
    /// closed: no rate row, and every levy and stamp (no rows exist, D-REG-06a), is <c>RULE_MISSING</c>; Production refuses a row that is
    /// not exactly Settled (PITFALLS 36). A credit base yields a credit tax only for a reducing transaction type.
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

        var lines = new List<TaxLine>(request.ChargeLines.Count);
        foreach (var line in request.ChargeLines)
        {
            lines.Add(CalculateLine(request, line));
        }

        return new TaxCalculationResult(lines, []);
    }

    private TaxLine CalculateLine(TaxCalculationRequest request, TaxChargeLine line)
    {
        if (string.IsNullOrWhiteSpace(line.TaxClass))
        {
            throw Validation("TAX_CLASS_REQUIRED", nameof(line.TaxClass));
        }

        if (string.IsNullOrWhiteSpace(line.Element) || string.IsNullOrWhiteSpace(line.ChargeType))
        {
            throw Validation("CHARGE_LINE_INCOMPLETE", nameof(line.Element));
        }

        if (line.PeriodEnd < line.PeriodStart)
        {
            throw Validation("PERIOD_INVALID", nameof(line.PeriodEnd));
        }

        if (!Currency.TryFromCode(line.PremiumAmount.Currency, out var currency))
        {
            throw Validation("CURRENCY_UNKNOWN", nameof(line.PremiumAmount));
        }

        // Levy and stamp have no rows (D-REG-06a, D-SL3-06): fail closed, never a default.
        var taxClass = line.TaxClass.Trim().ToLowerInvariant();
        if (taxClass.StartsWith("levy", StringComparison.Ordinal) || taxClass.StartsWith("stamp", StringComparison.Ordinal)
            || line.ChargeType.Contains("LEVY", StringComparison.OrdinalIgnoreCase) || line.ChargeType.Contains("STAMP", StringComparison.OrdinalIgnoreCase))
        {
            throw RuleMissing($"No rate rule for a levy or stamp (tax class {line.TaxClass}) in {request.RiskJurisdiction}; none is settled (D-REG-06a), the core holds no default (fail closed).");
        }

        var key = IptRatePrefix + taxClass;
        var entry = ConfigKeys.Find(key) is null
            ? null
            : engine.Catalogue.Find(key, request.RiskJurisdiction, new BusinessDate(request.TaxPointDate));
        if (entry is null)
        {
            throw RuleMissing($"No IPT rate {key} in {request.RiskJurisdiction} on {request.TaxPointDate:yyyy-MM-dd}; the core holds no default (fail closed).");
        }

        if (engine.EnforceSettled && entry.LegalStatus != LegalStatus.Settled)
        {
            throw new DomainException(DomainError.Of(
                ModuleCode.MKT, "CFG-NOT-SETTLED",
                $"Production refuses values that are not Settled (D-REG-02, REQ-MKT-343): {entry.Key} ({entry.LegalStatus})."));
        }

        var amount = line.PremiumAmount.Amount;
        if (amount < 0m && !ReducingTransactionTypes.Contains(line.TransactionType ?? string.Empty))
        {
            throw Validation("CREDIT_BASE_NOT_REDUCING", nameof(line.TransactionType));
        }

        if (!decimal.TryParse(entry.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) || rate < 0m || rate > 1m)
        {
            throw RuleMissing($"{entry.Key} does not hold a rate between 0 and 1; refused (fail closed).");
        }

        decimal tax;
        try
        {
            tax = Math.Round(amount * rate, currency.MinorUnits, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            throw Validation("AMOUNT_OUT_OF_RANGE", nameof(line.PremiumAmount));
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
            RoundingRuleId = "GR-IPT-ROUND-HALF_UP-MINOR_UNIT",
            RuleId = entry.Key,
            RuleVersion = entry.VersionId.ToString(),
            LegalSourceRef = entry.SourceRef,
            LegalStatus = entry.LegalStatus,
            ConfigurationHash = engine.Catalogue.Hash.Hash.ToString(),
        };
    }

    private const string IptRatePrefix = "tax.ipt.rate.";

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
