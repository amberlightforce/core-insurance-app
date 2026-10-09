using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Rating.Services;

/// <summary>
/// Tax lines for servicing premium deltas (REQ-RAT-009 subset, REQ-POL-124, D-SL3-05, D-SLC-11). For every delta RAT asks
/// <c>TaxCalculator.treatment</c> what happens to the tax, and never decides it itself:
/// <list type="bullet">
/// <item><c>APPLY</c> / <c>REDUCE_PRO_RATA</c> / <c>REVERSE_AS_VOID</c>: <c>TaxCalculator.calculate</c> once for the delta (single-call rule, REQ-MKT-332); the line carries the calculation and the treatment rule ids.</item>
/// <item><c>KEEP_NOT_REDUCED</c> / <c>INSURER_BEARS</c>: a line of 0.00 that carries the treatment, so the credit is explained.</item>
/// <item>Any SPI failure (RULE_MISSING, VALIDATION, timeout, contract violation) fails the whole request: no partial output.</item>
/// </list>
/// The legal status of a line is the weakest of calculation and treatment (a Pending treatment beats a Settled rate). A line that
/// is not Settled is <c>provisional</c> and is refused in Production (D-SLC-09).
/// </summary>
internal sealed class RatingServicingTax(
    ITaxCalculator calculator,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IHostEnvironment environment) : IRatingServicingTax
{
    private const int MaxDeltas = 400;

    public async Task<ServicingTaxLinesResult> TaxLinesAsync(ServicingTaxLinesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Deltas is null || request.Deltas.Count is 0 or > MaxDeltas)
        {
            throw Error("INPUT", $"Send 1 to {MaxDeltas} deltas.");
        }

        if (string.IsNullOrWhiteSpace(request.RiskJurisdiction) || string.IsNullOrWhiteSpace(request.ProductLine))
        {
            throw Error("INPUT", "The risk jurisdiction and the product line are required.");
        }

        var legalEntityId = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var holder = request.PolicyholderIsBusiness ? PolicyholderType.Business : PolicyholderType.Consumer;

        var lines = new List<ServicingTaxLine>(request.Deltas.Count);
        foreach (var delta in request.Deltas)
        {
            Validate(delta);
            lines.Add(await LineAsync(request, delta, legalEntityId, holder, cancellationToken).ConfigureAwait(false));
        }

        return new ServicingTaxLinesResult(lines);
    }

    private async Task<ServicingTaxLine> LineAsync(
        ServicingTaxLinesRequest request, ServicingDelta delta, Guid legalEntityId, PolicyholderType holder, CancellationToken cancellationToken)
    {
        var category = ToSpi(delta.Category);
        TaxTreatmentResult treatment;
        TaxLine? taxLine = null;
        try
        {
            treatment = await calculator.TreatmentAsync(
                new TaxTreatmentRequest
                {
                    LegalEntityId = legalEntityId,
                    RiskJurisdiction = request.RiskJurisdiction,
                    RiskSubdivision = request.RiskSubdivision,
                    TaxPointDate = request.TaxPointDate.Value,
                    ChargeType = delta.TaxChargeType,
                    Category = category,
                    ChargeOrigin = ChargeOrigin.Pol,
                    TransactionKind = ToSpi(delta.TransactionKind),
                    CancellationSource = delta.CancellationSource,
                    PolicyholderType = holder,
                    BusinessBasis = request.BusinessBasis,
                },
                cancellationToken).ConfigureAwait(false);

            if (treatment.Action is TreatmentAction.Apply or TreatmentAction.ReduceProRata or TreatmentAction.ReverseAsVoid)
            {
                var result = await calculator.CalculateAsync(
                    new TaxCalculationRequest
                    {
                        LegalEntityId = legalEntityId,
                        RiskJurisdiction = request.RiskJurisdiction,
                        RiskSubdivision = request.RiskSubdivision,
                        TaxPointDate = request.TaxPointDate.Value,
                        PolicyholderType = holder,
                        BusinessBasis = request.BusinessBasis,
                        TreatmentAction = treatment.Action,
                        ChargeLines =
                        [
                            new TaxChargeLine
                            {
                                Element = delta.Element,
                                ChargeType = delta.PremiumChargeType,
                                ChargeCategory = ChargeLineCategory.Premium,
                                ProductLine = request.ProductLine,
                                TaxClass = delta.TaxClass,
                                PremiumAmount = new SpiMoney(delta.Delta.Amount, delta.Delta.Currency.Code),
                                PeriodStart = delta.PeriodFrom.Value,
                                PeriodEnd = delta.PeriodTo.Value,
                                TransactionType = request.TransactionType,
                            },
                        ],
                    },
                    cancellationToken).ConfigureAwait(false);
                var matches = result.Lines.Where(l => l.ChargeType == delta.TaxChargeType && (l.Element is null || l.Element == delta.Element)).ToList();
                taxLine = matches.Count == 1
                    ? matches[0]
                    : throw Error("TAX", $"TaxCalculator returned {matches.Count} lines for {delta.TaxChargeType} on delta {delta.DeltaRef}; exactly one is expected (fail closed).");
            }
        }
        catch (SpiException ex)
        {
            // RULE_MISSING, VALIDATION, NOT_APPLICABLE, timeouts, contract violations: all fail the whole request (no partial output).
            throw Error("TAX", $"TaxCalculator refused delta {delta.DeltaRef} ({ex.Category}: {ex.Error.Code}); tax lines fail closed.");
        }
        catch (Exception ex) when (ex is not (DomainException or OperationCanceledException))
        {
            throw Error("TAX", $"TaxCalculator could not answer for delta {delta.DeltaRef} ({ex.GetType().Name}); tax lines fail closed.");
        }

        var amount = taxLine is null ? new Money(0m, delta.Delta.Currency) : ToMoney(taxLine.Amount, delta);
        if (taxLine is not null)
        {
            if (Math.Abs(amount.Amount) > Math.Abs(delta.Delta.Amount))
            {
                throw Error("TAX", $"TaxCalculator returned a tax larger than the delta {delta.DeltaRef}; refused (fail closed).");
            }

            if (treatment.Action == TreatmentAction.Apply && amount.Amount == 0m && delta.Delta.Amount != 0m)
            {
                throw Error("TAX", $"TaxCalculator returned 0.00 tax for delta {delta.DeltaRef} under APPLY; refused (fail closed).");
            }
        }

        if (taxLine is not null && amount.Amount != 0m && Math.Sign(amount.Amount) != Math.Sign(delta.Delta.Amount))
        {
            throw Error("TAX", $"TaxCalculator returned a tax of the opposite sign for delta {delta.DeltaRef}; refused (fail closed).");
        }

        // Only a Settled value is settled: NotRegulatory, Draft and anything unknown are provisional, and Production refuses them.
        var status = Weakest(taxLine?.LegalStatus.ToString(), treatment.LegalStatus == TreatmentLegalStatus.Settled ? "Settled" : "PendingOpinion");
        var provisional = status != "Settled";
        if (provisional && environment.IsProduction())
        {
            throw Error("TAX", $"The tax treatment or rate for delta {delta.DeltaRef} is {status}, which Production refuses (D-SLC-09).");
        }

        return new ServicingTaxLine(
            delta.DeltaRef,
            delta.Element,
            delta.TaxChargeType,
            delta.Category,
            delta.TaxClass,
            delta.Delta,
            taxLine?.Rate,
            amount,
            ToAction(treatment.Action),
            ToCredit(treatment.CustomerCredit),
            treatment.AuthorityLiability == AuthorityLiability.Reduce ? ServicingAuthorityLiability.Reduce : ServicingAuthorityLiability.NotReduce,
            treatment.FiscalDocument == FiscalDocumentTreatment.CreditNote ? ServicingFiscalDocument.CreditNote : ServicingFiscalDocument.None,
            taxLine?.RuleId,
            taxLine?.RuleVersion,
            treatment.RuleId,
            treatment.RuleVersion,
            status,
            provisional,
            taxLine?.LegalSourceRef ?? treatment.LegalSourceRef);
    }

    private static void Validate(ServicingDelta delta)
    {
        if (string.IsNullOrWhiteSpace(delta.DeltaRef) || string.IsNullOrWhiteSpace(delta.Element) || string.IsNullOrWhiteSpace(delta.TaxChargeType)
            || string.IsNullOrWhiteSpace(delta.PremiumChargeType))
        {
            throw Error("INPUT", "Every delta needs a reference, an element, a premium charge type and a tax charge type.");
        }

        if (string.IsNullOrWhiteSpace(delta.TaxClass))
        {
            throw Error("TAX", $"Delta {delta.DeltaRef} has no tax class; there is no default (D-REG-01).");
        }

        if (delta.PeriodTo < delta.PeriodFrom)
        {
            throw Error("PERIOD", $"Delta {delta.DeltaRef} ends before it starts.");
        }

        if (delta.TransactionKind is ServicingTransactionKind.Cancellation or ServicingTransactionKind.Void && string.IsNullOrWhiteSpace(delta.CancellationSource))
        {
            throw Error("INPUT", $"Delta {delta.DeltaRef} needs a cancellation source for a {delta.TransactionKind}.");
        }
    }

    private static Money ToMoney(SpiMoney money, ServicingDelta delta) =>
        money.Currency == delta.Delta.Currency.Code
            ? new Money(money.Amount, delta.Delta.Currency)
            : throw Error("CURRENCY", $"TaxCalculator answered in {money.Currency}, not {delta.Delta.Currency.Code}, for delta {delta.DeltaRef}.");

    /// <summary>The weaker of two statuses. Settled is the only strong one; every other value, NotRegulatory included, is weaker.</summary>
    private static string Weakest(string? calculation, string treatment) =>
        calculation is null || Rank(treatment) >= Rank(calculation) ? treatment : calculation;

    private static int Rank(string status) => status switch
    {
        "Settled" => 0,
        "NotRegulatory" => 1,
        "Verify" => 2,
        "PendingOpinion" => 3,
        "Uncertain" or "MarketPractice" => 4,
        "Unverified" => 5,
        "Draft" => 6,
        _ => 7,
    };

    private static ServicingCustomerCredit ToCredit(CustomerCredit credit) => credit switch
    {
        CustomerCredit.ProRata => ServicingCustomerCredit.ProRata,
        CustomerCredit.Full => ServicingCustomerCredit.Full,
        _ => ServicingCustomerCredit.None,
    };

    private static TaxCategory ToSpi(ServicingTaxCategory category) => category switch
    {
        ServicingTaxCategory.Tax => TaxCategory.Tax,
        ServicingTaxCategory.Levy => TaxCategory.Levy,
        ServicingTaxCategory.Stamp => TaxCategory.Stamp,
        _ => throw Error("INPUT", "Unknown tax category."),
    };

    private static TaxTransactionKind ToSpi(ServicingTransactionKind kind) => kind switch
    {
        ServicingTransactionKind.NewBusiness => TaxTransactionKind.NewBusiness,
        ServicingTransactionKind.EndorsementDebit => TaxTransactionKind.EndorsementDebit,
        ServicingTransactionKind.EndorsementCredit => TaxTransactionKind.EndorsementCredit,
        ServicingTransactionKind.Cancellation => TaxTransactionKind.Cancellation,
        ServicingTransactionKind.DistanceWithdrawalVoid => TaxTransactionKind.DistanceWithdrawalVoid,
        ServicingTransactionKind.Void => TaxTransactionKind.Void,
        ServicingTransactionKind.ReturnPremium => TaxTransactionKind.ReturnPremium,
        ServicingTransactionKind.Reinstatement => TaxTransactionKind.Reinstatement,
        ServicingTransactionKind.Fee => TaxTransactionKind.Fee,
        ServicingTransactionKind.Refund => TaxTransactionKind.Refund,
        _ => throw Error("INPUT", "Unknown transaction kind."),
    };

    private static ServicingTreatmentAction ToAction(TreatmentAction action) => action switch
    {
        TreatmentAction.Apply => ServicingTreatmentAction.Apply,
        TreatmentAction.ReduceProRata => ServicingTreatmentAction.ReduceProRata,
        TreatmentAction.ReverseAsVoid => ServicingTreatmentAction.ReverseAsVoid,
        TreatmentAction.KeepNotReduced => ServicingTreatmentAction.KeepNotReduced,
        TreatmentAction.InsurerBears => ServicingTreatmentAction.InsurerBears,
        _ => throw Error("TAX", "TaxCalculator returned a treatment action RAT does not know (fail closed)."),
    };

    private static DomainException Error(string code, string detail) => new(DomainError.Of(ModuleCode.RAT, code, detail));
}
