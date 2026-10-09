using System.Text.Json;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using RatDelta = CoreIns.Modules.Rating.Contracts.Servicing.ServicingDelta;

namespace CoreIns.Modules.Policy.Commands.Change;

/// <summary>A tax port that learns which pinned product artefact it taxes for (as the proration does).</summary>
internal interface IBoundServicingTax
{
    /// <summary>Binds the port to the term's pinned product artefact (by hash) before the engine runs.</summary>
    void Bind(string productArtefactHash);
}

/// <summary>
/// The production <see cref="IServicingTax"/> (SL3-E2E integration fix; SL3-POL-WIRING): the tax lines of a change's premium deltas come
/// from RAT's servicing tax (<see cref="IRatingServicingTax"/>), which asks MKT's <c>TaxCalculator.treatment</c> and, where a tax applies,
/// <c>calculate</c>. POL computes no tax: it reads the IPT class from MKT configuration and, from the term's pinned product artefact, the tax charge types that charge is included in (category TAX only; the slice has no levy line, D-REG-06a), and maps the answer.
/// Fails closed on anything missing (PITFALLS 10).
/// </summary>
internal sealed class RatingServicingTaxAdapter(
    IRatingServicingTax rating, Dependency<IProductArtifactService> products, Dependency<IMarketConfigurationService> marketConfiguration) : IServicingTax, IBoundServicingTax
{
    private string? _artefactHash;

    public void Bind(string productArtefactHash) => _artefactHash = productArtefactHash;

    public async Task<Result<IReadOnlyList<PricedTaxLine>>> LinesAsync(ServicingTaxRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_artefactHash is null)
        {
            return Refused("the tax port is not bound to a term's product artefact");
        }

        JsonElement artefact;
        try
        {
            artefact = (await products.Value.GetAsync(_artefactHash, cancellationToken: cancellationToken).ConfigureAwait(false)).CanonicalJsonArtefact
                ?? throw new DomainException(Refused("the pinned product artefact has no content").Error!);
        }
        catch (DomainException)
        {
            return Refused("the term's pinned product artefact cannot be read");
        }

        var productLine = artefact.TryGetProperty("productLine", out var pl) && pl.TryGetProperty("code", out var plCode) ? plCode.GetString() : null;
        if (string.IsNullOrWhiteSpace(productLine) || !artefact.TryGetProperty("chargeTypes", out var chargeTypes) || chargeTypes.ValueKind != JsonValueKind.Array)
        {
            return Refused("the pinned product artefact has no product line or charge types");
        }

        // The tax class is MKT configuration (tax.ipt.motor_class), the same source the rating artefact reads at issuance; the
        // artefact's own class label is not a key of the pack (D-REG-01: no default, fail closed).
        var productCode = artefact.TryGetProperty("product", out var pr) && pr.TryGetProperty("code", out var prCode) ? prCode.GetString() : null;
        string motorClass;
        try
        {
            var resolved = await marketConfiguration.Value.ResolveAsync(
                new ConfigurationResolveRequest
                {
                    LegalEntity = request.LegalEntity,
                    Jurisdiction = request.Jurisdiction,
                    ProductCode = productCode,
                    Channel = "STAFF",
                    TimeBasisDates = new Dictionary<string, BusinessDate> { ["TAX_POINT_DATE"] = request.TaxPointDate, ["EFFECTIVE_DATE"] = request.TaxPointDate },
                    Keys = ["tax.ipt.motor_class"],
                },
                ValidAt.From(request.TaxPointDate), cancellationToken: cancellationToken).ConfigureAwait(false);
            var value = resolved.Values.FirstOrDefault(v => v.Key == "tax.ipt.motor_class");
            motorClass = value is { Value.ValueKind: JsonValueKind.String } ? value.Value.GetString()!.Trim().ToLowerInvariant() : string.Empty;
        }
        catch (DomainException)
        {
            return Refused("MKT configuration could not resolve the IPT class");
        }

        if (motorClass.Length == 0)
        {
            return Refused("MKT configuration holds no IPT class (tax.ipt.motor_class)");
        }

        var byCode = chargeTypes.EnumerateArray().Where(c => c.TryGetProperty("code", out _)).ToDictionary(c => c.GetProperty("code").GetString()!, c => c, StringComparer.Ordinal);
        var items = new List<(Domain.Servicing.ServicingDelta Delta, RatDelta Rat)>();
        foreach (var delta in request.PremiumDeltas)
        {
            if (!byCode.TryGetValue(delta.Key.ChargeType, out var premium)
                || !premium.TryGetProperty("includedInTaxBases", out var bases) || bases.ValueKind != JsonValueKind.Array)
            {
                return Refused($"charge type {delta.Key.ChargeType} declares no tax bases");
            }

            var taxTypes = bases.EnumerateArray().Select(b => b.GetString()!)
                .Where(code => byCode.TryGetValue(code, out var t) && t.TryGetProperty("category", out var c) && c.GetString() == "TAX").ToList();
            if (taxTypes.Count != 1)
            {
                return Refused($"charge type {delta.Key.ChargeType} has {taxTypes.Count} tax charge types; exactly one is expected");
            }

            items.Add((delta, new RatDelta(
                DeltaRef: items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Element: delta.Key.CoverageCode,
                PremiumChargeType: delta.Key.ChargeType,
                TaxChargeType: taxTypes[0],
                Category: ServicingTaxCategory.Tax,
                TaxClass: motorClass,
                Delta: new Money(delta.Amount, request.Currency),
                PeriodFrom: new BusinessDate(delta.DateFrom),
                PeriodTo: new BusinessDate(delta.DateTo),
                TransactionKind: delta.TransactionKind == TransactionKind.EndorsementCredit ? ServicingTransactionKind.EndorsementCredit : ServicingTransactionKind.EndorsementDebit)));
        }

        ServicingTaxLinesResult result;
        try
        {
            result = await rating.TaxLinesAsync(
                new ServicingTaxLinesRequest(request.Jurisdiction, null, request.TaxPointDate, "ENDORSEMENT", productLine, [.. items.Select(i => i.Rat)]), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return DomainError.Of(ModuleCode.POL, "RATING", $"Servicing tax lines failed: {ex.Error}");
        }

        return MapLines(items, result);
    }

    internal static Result<IReadOnlyList<PricedTaxLine>> MapLines(
        IReadOnlyList<(Domain.Servicing.ServicingDelta Delta, RatDelta Rat)> items, ServicingTaxLinesResult result)
    {
        if (result.Lines.Count != items.Count)
        {
            return Refused("RAT returned a different number of tax lines than deltas");
        }

        var lines = new List<PricedTaxLine>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var line = result.Lines[i];
            var expected = items[i].Rat;
            if (line.DeltaRef != expected.DeltaRef || line.Element != expected.Element || line.TaxChargeType != expected.TaxChargeType
                || line.Category != expected.Category || line.TaxClass != expected.TaxClass || line.Base != expected.Delta
                || line.Amount.Currency != expected.Delta.Currency)
            {
                return Refused("RAT returned a tax line that does not match its premium delta");
            }

            if (!Enum.IsDefined(line.TreatmentAction))
            {
                return Refused("RAT returned an unknown treatment action");
            }

            var calculable = line.TreatmentAction is ServicingTreatmentAction.Apply or ServicingTreatmentAction.ReduceProRata or ServicingTreatmentAction.ReverseAsVoid;
            if (decimal.Round(line.Amount.Amount, line.Amount.Currency.MinorUnits) != line.Amount.Amount
                || Math.Abs(line.Amount.Amount) > Math.Abs(expected.Delta.Amount)
                || (line.Amount.Amount != 0m && Math.Sign(line.Amount.Amount) != Math.Sign(expected.Delta.Amount))
                || (!calculable && line.Amount.Amount != 0m))
            {
                return Refused("RAT returned an invalid tax amount for its premium delta");
            }

            if (calculable && (line.Rate is null || line.Rate < 0m || line.Rate > 1m
                || string.IsNullOrWhiteSpace(line.CalculationRuleId) || string.IsNullOrWhiteSpace(line.CalculationRuleVersion)))
            {
                return Refused("RAT returned a calculated tax without rate or rule evidence");
            }

            if (string.IsNullOrWhiteSpace(line.TreatmentRuleId) || string.IsNullOrWhiteSpace(line.TreatmentRuleVersion)
                || string.IsNullOrWhiteSpace(line.LegalStatus) || line.Provisional != (line.LegalStatus != "Settled"))
            {
                return Refused("RAT returned incomplete or inconsistent treatment evidence");
            }

            var action = line.TreatmentAction switch
            {
                ServicingTreatmentAction.Apply => TreatmentActionCode.Apply,
                ServicingTreatmentAction.KeepNotReduced => TreatmentActionCode.KeepNotReduced,
                ServicingTreatmentAction.ReduceProRata => TreatmentActionCode.ReduceProRata,
                ServicingTreatmentAction.ReverseAsVoid => TreatmentActionCode.ReverseAsVoid,
                ServicingTreatmentAction.InsurerBears => TreatmentActionCode.InsurerBears,
                _ => throw new InvalidOperationException("The treatment action was validated above."),
            };
            lines.Add(new PricedTaxLine(
                items[i].Delta.Key, items[i].Delta.Key.CoverageCode, line.TaxChargeType, "TAX", line.Rate ?? 0m, line.Amount.Amount, action,
                line.TreatmentRuleId, line.TreatmentRuleVersion, line.LegalStatus, line.Provisional));
        }

        return lines;
    }

    private static Result<IReadOnlyList<PricedTaxLine>> Refused(string reason) =>
        DomainError.Of(ModuleCode.POL, "RATING", $"Servicing tax lines are refused: {reason}.");
}
