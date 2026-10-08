using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Market.Contracts.Spi;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>The outcome of the tax-treatment check: null reason means the entry may be mapped.</summary>
internal sealed record TaxCheckResult(string? Detail)
{
    public static TaxCheckResult Ok { get; } = new(Detail: null);

    public bool Passed => Detail is null;

    public static TaxCheckResult Fail(string detail) => new(detail);
}

/// <summary>
/// The REQ-FIN-182 / -183 check (D-SL3-05): a servicing entry (credit, endorsement, refund) may only move a tax or levy
/// payable the way <c>TaxCalculator.treatment</c> says. For each non-zero tax, levy or stamp line FIN calls the treatment
/// with the line's transaction kind, cancellation source and charge type, then compares:
/// <list type="bullet">
/// <item>a debit on the payable (a reduction of the authority liability) where the treatment says NOT_REDUCE
/// (APPLY, KEEP_NOT_REDUCED, INSURER_BEARS) fails;</item>
/// <item>a credit kept on the payable where the treatment says REDUCE (REDUCE_PRO_RATA, REVERSE_AS_VOID) fails;</item>
/// <item>RULE_MISSING (no rule, or no rule for that source), any other SPI error, a Production refusal, an unknown
/// transaction kind and a calculator that is not bound all fail: the core holds no default and never guesses;</item>
/// <item>a servicing entry without <c>transactionKind</c> on a line fails (PITFALLS 10), and so does a premium entry
/// (WRITTEN, BILLED) that reduces a tax payable without declaring a transaction kind.</item>
/// </list>
/// A failure suspends the whole event as TAX_RULE_VIOLATION: no journal, no suspense account (REQ-FIN-084). Pure with
/// respect to FIN's data: it reads only the entry and the MKT SPI (a pure call, REQ-MKT-117).
/// </summary>
internal sealed class TaxTreatmentCheck(IServiceProvider services)
{
    /// <summary>Policyholder type sent to <c>treatment</c>: the entry does not carry it and the Greece rows do not depend on it.</summary>
    private const PolicyholderType AssumedPolicyholder = PolicyholderType.Consumer;

    /// <summary>Business basis sent to <c>treatment</c>: the entry does not carry it and the Greece rows do not depend on it.</summary>
    private const string AssumedBusinessBasis = "ESTABLISHMENT";

    public async Task<TaxCheckResult> CheckAsync(
        string entryType, SourceEntry entry, Guid legalEntityId, string jurisdiction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var declared = entry.Lines.Any(l => l.Dimension(LineDimensionKeys.TransactionKind) is not null);
        var servicing = declared || ServicingEntryTypes.All.Contains(entryType, StringComparer.Ordinal);
        if (!servicing)
        {
            // Slice-2 premium and cash entries need no treatment. A premium entry that reduces a tax payable is a reversal
            // in disguise: it must say what transaction it belongs to (fail closed, PITFALLS 10).
            var reduction = ServicingEntryTypes.Premium.Contains(entryType, StringComparer.Ordinal)
                ? entry.Lines.FirstOrDefault(l => IsPayable(l) && !l.Amount.IsZero && IsReduction(l))
                : null;
            return reduction is null
                ? TaxCheckResult.Ok
                : TaxCheckResult.Fail($"{entryType} entry reduces {reduction.Account} without a transactionKind; the tax treatment cannot be checked (REQ-FIN-182).");
        }

        foreach (var line in entry.Lines)
        {
            if (line.Dimension(LineDimensionKeys.TransactionKind) is null)
            {
                return TaxCheckResult.Fail($"Servicing entry {entryType} has a line on {line.Account} without transactionKind (REQ-FIN-182).");
            }
        }

        var taxLines = entry.Lines.Where(l => IsTaxLine(l) && !l.Amount.IsZero).ToList();
        if (taxLines.Count == 0)
        {
            return TaxCheckResult.Ok;
        }

        var calculator = services.GetService<ITaxCalculator>();
        if (calculator is null)
        {
            return TaxCheckResult.Fail("TaxCalculator.treatment is not bound in this stamp; a tax or levy movement on a servicing entry cannot be validated (REQ-FIN-182).");
        }

        var results = new Dictionary<TaxTreatmentRequest, TaxTreatmentResult>();
        foreach (var line in taxLines)
        {
            var request = RequestOf(line, legalEntityId, jurisdiction, entry);
            if (request.Problem is { } problem)
            {
                return TaxCheckResult.Fail(problem);
            }

            if (!results.TryGetValue(request.Request!, out var treatment))
            {
                try
                {
                    treatment = await calculator.TreatmentAsync(request.Request!, cancellationToken).ConfigureAwait(false);
                }
                catch (SpiException ex) when (ex.Error.Category == SpiErrorCategory.RuleMissing)
                {
                    return TaxCheckResult.Fail($"RULE_MISSING: no tax treatment for {request.Request!.Category} {request.Request.ChargeType} on {request.Request.TransactionKind}"
                        + $"{(request.Request.CancellationSource is { } s ? " (source " + s + ")" : string.Empty)}; the credit cannot be validated (REQ-FIN-182, D-SL3-06).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Validation errors, a Production refusal of a provisional rule, a timeout: none of them is a reason to post.
                    return TaxCheckResult.Fail($"TaxCalculator.treatment failed ({ex.GetType().Name}) for {request.Request!.Category} {request.Request.ChargeType}: {Truncate(ex.Message)}");
                }

                results[request.Request!] = treatment;
            }

            var reduces = IsReduction(line);
            var mustReduce = treatment.AuthorityLiability == AuthorityLiability.Reduce;
            if (IsPayable(line) && reduces && !mustReduce)
            {
                return TaxCheckResult.Fail($"{entryType} reduces {line.Account} by {line.Amount.Abs()} but the treatment {treatment.RuleId} ({treatment.Action}) does not reduce the authority liability "
                    + $"(transaction {request.Request!.TransactionKind}, source {request.Request.CancellationSource ?? "-"}; REQ-FIN-182, -183).");
            }

            if (IsPayable(line) && !reduces && mustReduce)
            {
                return TaxCheckResult.Fail($"{entryType} keeps {line.Account} ({line.Amount.Abs()}) but the treatment {treatment.RuleId} ({treatment.Action}) reduces the authority liability "
                    + $"(transaction {request.Request!.TransactionKind}, source {request.Request.CancellationSource ?? "-"}; REQ-FIN-182, -183).");
            }
        }

        return TaxCheckResult.Ok;
    }

    private static (TaxTreatmentRequest? Request, string? Problem) RequestOf(SourceLine line, Guid legalEntityId, string jurisdiction, SourceEntry entry)
    {
        var kindCode = line.Dimension(LineDimensionKeys.TransactionKind)!;
        if (Kind(kindCode) is not { } kind)
        {
            return (null, $"Line on {line.Account} has an unknown transactionKind '{kindCode}' (REQ-FIN-182).");
        }

        var category = line.Dimension(LineDimensionKeys.ChargeCategory) switch
        {
            TaxPayableAccounts.Tax => (TaxCategory?)TaxCategory.Tax,
            TaxPayableAccounts.Levy => TaxCategory.Levy,
            TaxPayableAccounts.Stamp => TaxCategory.Stamp,
            _ => null,
        };
        if (category is null)
        {
            return (null, $"Line on {line.Account} has no tax, levy or stamp chargeCategory; the treatment cannot be looked up (REQ-FIN-182).");
        }

        var chargeType = line.Dimension(LineDimensionKeys.ChargeType);
        if (chargeType is null)
        {
            return (null, $"Line on {line.Account} has no chargeType; the treatment cannot be looked up (REQ-FIN-182).");
        }

        return (new TaxTreatmentRequest
        {
            LegalEntityId = legalEntityId,
            RiskJurisdiction = jurisdiction,
            TaxPointDate = entry.AccountingDate.Value,
            ChargeType = chargeType,
            Category = category.Value,
            ChargeOrigin = ChargeOrigin.Bil,
            TransactionKind = kind,
            CancellationSource = line.Dimension(LineDimensionKeys.CancellationSource),
            PolicyholderType = AssumedPolicyholder,
            BusinessBasis = AssumedBusinessBasis,
        }, null);
    }

    private static TaxTransactionKind? Kind(string code) => code switch
    {
        "NEW_BUSINESS" => TaxTransactionKind.NewBusiness,
        "ENDORSEMENT_DEBIT" => TaxTransactionKind.EndorsementDebit,
        "ENDORSEMENT_CREDIT" => TaxTransactionKind.EndorsementCredit,
        "CANCELLATION" => TaxTransactionKind.Cancellation,
        "DISTANCE_WITHDRAWAL_VOID" => TaxTransactionKind.DistanceWithdrawalVoid,
        "VOID" => TaxTransactionKind.Void,
        "RETURN_PREMIUM" => TaxTransactionKind.ReturnPremium,
        "REINSTATEMENT" => TaxTransactionKind.Reinstatement,
        "FEE" => TaxTransactionKind.Fee,
        "REFUND" => TaxTransactionKind.Refund,
        _ => null,
    };

    /// <summary>A line of a tax, levy or stamp charge, or on a tax payable account.</summary>
    private static bool IsTaxLine(SourceLine line) =>
        IsPayable(line) || line.Dimension(LineDimensionKeys.ChargeCategory) is TaxPayableAccounts.Tax or TaxPayableAccounts.Levy or TaxPayableAccounts.Stamp;

    private static bool IsPayable(SourceLine line) => TaxPayableAccounts.SubLedger.Contains(line.Account, StringComparer.Ordinal);

    /// <summary>
    /// True when the line debits a payable (a liability, credit-normal): the authority liability goes down. A negative
    /// amount posts on the opposite side, as <see cref="EntryPosting"/> does.
    /// </summary>
    private static bool IsReduction(SourceLine line) =>
        (line.Amount.IsNegative ? Sides.Opposite(line.Side) : line.Side) == Sides.Debit;

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200];
}
