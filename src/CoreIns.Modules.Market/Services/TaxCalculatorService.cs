using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// SPI 4 <see cref="ITaxCalculator"/> as bound by MKT (PRD-17 section 9.4.4): <c>treatment</c> (D2, REQ-MKT-330/331/332)
/// over the treatment rule rows of the country layer. Pure and fail closed: no rule row means <c>RULE_MISSING</c> and the core
/// holds no default. Rows are pack data with a legal status; the result is <c>provisional</c> when that status is not Settled,
/// and Production refuses a non-Settled row (D-REG-02, D-SLC-09) through the same gate as the configuration resolver.
/// <c>calculate</c> arrives with the rate work packages and is not bound here.
/// </summary>
internal sealed class MarketTaxCalculator(ConfigurationEngine engine) : ITaxCalculator
{
    public ValueTask<TaxCalculationResult> CalculateAsync(TaxCalculationRequest request, CancellationToken cancellationToken = default) =>
        throw new SpiException(
            new SpiError(SpiErrorCategory.NotApplicable, "CALCULATE_NOT_BOUND"),
            "tax calculate is not provided by this work package; only treatment is bound.");

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

            if (!TaxTreatmentRules.CancellationSources.Contains(request.CancellationSource))
            {
                throw Validation("CANCELLATION_SOURCE_UNKNOWN", nameof(request.CancellationSource));
            }

            source = request.CancellationSource;
        }

        var key = TaxTreatmentRules.Key(request.Category, request.TransactionKind, source);
        var entry = engine.Catalogue.Find(key, request.RiskJurisdiction, new BusinessDate(request.TaxPointDate));
        if (entry is null)
        {
            throw new SpiException(
                new SpiError(SpiErrorCategory.RuleMissing, "RULE_MISSING"),
                $"No treatment rule for {key} in {request.RiskJurisdiction} on {request.TaxPointDate:yyyy-MM-dd}; the core holds no default (fail closed).");
        }

        if (engine.EnforceSettled && !entry.IsSettled)
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
            LegalStatus = entry.IsSettled ? TreatmentLegalStatus.Settled : TreatmentLegalStatus.Pending,
            LegalSourceRef = entry.SourceRef,
        };
    }

    private static SpiException Validation(string code, string field) =>
        new(new SpiError(SpiErrorCategory.Validation, code, field), $"MKT-ERR-SPI-VALIDATION: {code} ({field}).");
}
