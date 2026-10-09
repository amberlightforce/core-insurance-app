using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Finance.Posting;

/// <summary>The outcome of the tax-treatment check: no detail means the entry may be posted.</summary>
internal sealed record TaxCheckResult(string? Detail)
{
    public static TaxCheckResult Ok { get; } = new(Detail: null);

    public bool Passed => Detail is null;

    public static TaxCheckResult Fail(string detail) => new(detail);
}

/// <summary>
/// The REQ-FIN-182 / -183 check (D-SL3-05). Any entry that moves a tax, levy or stamp payable is validated against
/// <c>TaxCalculator.treatment</c>, at two points so neither the BIL account nor the PFC GL key can be used to slip past it:
/// <list type="number">
/// <item><b>Source</b> (<see cref="CheckSourceAsync"/>, before rule lookup): the BIL sub-ledger accounts LA-06/LA-27 (IPT),
/// LA-07 (levy) and LA-26 (stamp). It also catches lines for which no rule exists.</item>
/// <item><b>Journal</b> (<see cref="CheckJournalAsync"/>, after mapping): the <i>resolved</i> accounts GL-2410/2411, GL-2420
/// and GL-2425. A premium line carrying the IPT charge type is derived to GL-2410 by its PFC GL key; here it is seen as the
/// IPT movement it is, whatever its BIL account or category said.</item>
/// </list>
/// A movement is the net of an entry per (category, charge type, transaction kind, cancellation source): a credit that
/// grows the payable is an increase, a debit that shrinks it a reduction, a transfer inside the payable (IPT_DUE moves
/// GL-2411 to GL-2410, the normal Greek DUE path) nets to zero and is no reduction. Rules:
/// <list type="bullet">
/// <item>a reduction where the treatment says NOT_REDUCE (APPLY, KEEP_NOT_REDUCED, INSURER_BEARS) fails; an increase where
/// it says REDUCE (REDUCE_PRO_RATA, REVERSE_AS_VOID) fails;</item>
/// <item>a reduction without a transaction kind fails, whatever the entry type (no entry type may reduce a payable without
/// passing the check; FIN has no remittance rule yet);</item>
/// <item>IPT_DUE may never debit IPT payable (LA-06/GL-2410): it only releases the not-due IPT into it;</item>
/// <item>RULE_MISSING, any other SPI error, a Production refusal, an unknown kind, a missing charge type, an unbound
/// calculator, and a treatment that depends on policyholder type or business basis (which BIL does not carry) all fail;</item>
/// <item>a credit, endorsement or refund entry without <c>transactionKind</c> on every line fails (PITFALLS 10).</item>
/// </list>
/// A failure suspends the whole event as TAX_RULE_VIOLATION: no journal, no suspense account (REQ-FIN-084).
/// </summary>
internal sealed class TaxTreatmentCheck(IServiceProvider services)
{
    private const PolicyholderType AssumedPolicyholder = PolicyholderType.Consumer;
    private const string AssumedBusinessBasis = "ESTABLISHMENT";
    private const string IptDue = "IPT_DUE";

    /// <summary>A net movement of one tax, levy or stamp payable (credit positive).</summary>
    private sealed record Movement(TaxCategory Category, string? ChargeType, string? Kind, string? Source, decimal Net, string Account);

    public async Task<TaxCheckResult> CheckSourceAsync(
        string entryType, SourceEntry entry, Guid legalEntityId, string jurisdiction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (ServicingEntryTypes.All.Contains(entryType, StringComparer.Ordinal))
        {
            foreach (var line in entry.Lines)
            {
                if (line.Dimension(LineDimensionKeys.TransactionKind) is null)
                {
                    return TaxCheckResult.Fail($"Servicing entry {entryType} has a line on {line.Account} without transactionKind (REQ-FIN-182).");
                }
            }
        }

        var items = new List<(string Account, TaxCategory? Category, string Side, decimal Amount, string? ChargeType, string? Kind, string? Source)>();
        foreach (var line in entry.Lines.Where(l => !l.Amount.IsZero))
        {
            var side = line.Amount.IsNegative ? Sides.Opposite(line.Side) : line.Side;
            items.Add((line.Account, SourceCategory(line.Account), side, line.Amount.Abs().Amount, line.Dimension(LineDimensionKeys.ChargeType),
                line.Dimension(LineDimensionKeys.TransactionKind), line.Dimension(LineDimensionKeys.CancellationSource)));
        }

        if (entryType == IptDue && items.Any(i => i.Account == "LA-06" && i.Side == Sides.Debit))
        {
            return TaxCheckResult.Fail("IPT_DUE debits the IPT payable LA-06: it may only release the not-yet-due IPT into it (REQ-FIN-182).");
        }

        var movements = Movements(items.Where(i => i.Category is not null).Select(i => (i.Account, i.Category!.Value, i.Side, i.Amount, i.ChargeType, i.Kind, i.Source)));
        return await EvaluateAsync(entryType, movements, entry.AccountingDate, legalEntityId, jurisdiction, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaxCheckResult> CheckJournalAsync(
        string entryType, JournalDraft draft, Guid legalEntityId, string jurisdiction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var items = draft.Lines.Where(l => ResolvedCategory(l.Account) is not null)
            .Select(l => (l.Account, ResolvedCategory(l.Account)!.Value, l.Side, l.Amount.Amount, l.Dimensions.ChargeType, l.Dimensions.TransactionKind, l.Dimensions.CancellationSource))
            .ToList();
        if (entryType == IptDue && items.Any(i => i.Account == "GL-2410" && i.Side == Sides.Debit))
        {
            return TaxCheckResult.Fail("IPT_DUE debits the IPT payable GL-2410: it may only release the not-yet-due IPT into it (REQ-FIN-182).");
        }

        return await EvaluateAsync(entryType, Movements(items), draft.AccountingDate, legalEntityId, jurisdiction, cancellationToken).ConfigureAwait(false);
    }

    private static List<Movement> Movements(IEnumerable<(string Account, TaxCategory Category, string Side, decimal Amount, string? ChargeType, string? Kind, string? Source)> items) =>
        [.. items.GroupBy(i => (i.Category, i.ChargeType, i.Kind, i.Source))
            .Select(g => new Movement(g.Key.Category, g.Key.ChargeType, g.Key.Kind, g.Key.Source,
                g.Sum(i => i.Side == Sides.Credit ? i.Amount : -i.Amount), g.First().Account))];

    private async Task<TaxCheckResult> EvaluateAsync(
        string entryType, List<Movement> movements, BusinessDate accountingDate, Guid legalEntityId, string jurisdiction, CancellationToken cancellationToken)
    {
        ITaxCalculator? calculator = null;
        foreach (var movement in movements.Where(m => m.Net != 0m))
        {
            var reduces = movement.Net < 0m;
            if (movement.Kind is null)
            {
                if (reduces)
                {
                    return TaxCheckResult.Fail($"{entryType} reduces the {movement.Category} payable ({movement.Account}) by {-movement.Net} without a transactionKind; "
                        + "the treatment cannot be checked (REQ-FIN-182).");
                }

                continue; // slice-2 new business: an increase needs no treatment
            }

            if (Kind(movement.Kind) is not { } kind)
            {
                return TaxCheckResult.Fail($"{entryType} has an unknown transactionKind '{movement.Kind}' on {movement.Account} (REQ-FIN-182).");
            }

            if (movement.ChargeType is null)
            {
                return TaxCheckResult.Fail($"{entryType} moves the {movement.Category} payable ({movement.Account}) without a chargeType; the treatment cannot be looked up (REQ-FIN-182).");
            }

            calculator ??= services.GetService<ITaxCalculator>();
            if (calculator is null)
            {
                return TaxCheckResult.Fail("TaxCalculator.treatment is not bound in this stamp; a tax or levy movement on a servicing entry cannot be validated (REQ-FIN-182).");
            }

            var request = new TaxTreatmentRequest
            {
                LegalEntityId = legalEntityId,
                RiskJurisdiction = jurisdiction,
                TaxPointDate = accountingDate.Value,
                ChargeType = movement.ChargeType,
                Category = movement.Category,
                ChargeOrigin = ChargeOrigin.Bil,
                TransactionKind = kind,
                CancellationSource = movement.Source,
                PolicyholderType = AssumedPolicyholder,
                BusinessBasis = AssumedBusinessBasis,
            };
            var (treatment, problem) = await TreatmentAsync(calculator, request, cancellationToken).ConfigureAwait(false);
            if (treatment is null)
            {
                return TaxCheckResult.Fail(problem!);
            }

            var mustReduce = treatment.AuthorityLiability == AuthorityLiability.Reduce;
            if (reduces && !mustReduce)
            {
                return TaxCheckResult.Fail($"{entryType} reduces {movement.Account} by {-movement.Net} but the treatment {treatment.RuleId} ({treatment.Action}) does not reduce the authority liability "
                    + $"(transaction {movement.Kind}, source {movement.Source ?? "-"}; REQ-FIN-182, -183).");
            }

            if (!reduces && mustReduce)
            {
                return TaxCheckResult.Fail($"{entryType} keeps {movement.Account} (+{movement.Net}) but the treatment {treatment.RuleId} ({treatment.Action}) reduces the authority liability "
                    + $"(transaction {movement.Kind}, source {movement.Source ?? "-"}; REQ-FIN-182, -183).");
            }
        }

        return TaxCheckResult.Ok;
    }

    /// <summary>
    /// Calls <c>treatment</c> and proves the answer does not depend on policyholder type or business basis, which BIL does
    /// not carry: the same request with the other policyholder type and another basis must give the same action and rule.
    /// </summary>
    private static async Task<(TaxTreatmentResult? Result, string? Problem)> TreatmentAsync(ITaxCalculator calculator, TaxTreatmentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await calculator.TreatmentAsync(request, cancellationToken).ConfigureAwait(false);
            foreach (var variant in new[]
            {
                request with { PolicyholderType = PolicyholderType.Business },
                request with { BusinessBasis = "FREEDOM_OF_SERVICES" },
            })
            {
                var other = await calculator.TreatmentAsync(variant, cancellationToken).ConfigureAwait(false);
                if (other.Action != result.Action || other.AuthorityLiability != result.AuthorityLiability || other.RuleId != result.RuleId)
                {
                    return (null, $"The treatment of {request.Category} {request.ChargeType} on {request.TransactionKind} depends on policyholder type or business basis, "
                        + "which the BIL entry does not carry; FIN cannot validate it (fail closed, REQ-FIN-182).");
                }
            }

            return (result, null);
        }
        catch (SpiException ex) when (ex.Error.Category == SpiErrorCategory.RuleMissing)
        {
            return (null, $"RULE_MISSING: no tax treatment for {request.Category} {request.ChargeType} on {request.TransactionKind}"
                + $"{(request.CancellationSource is { } s ? " (source " + s + ")" : string.Empty)}; the entry cannot be validated (REQ-FIN-182, D-SL3-06).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Validation errors, a Production refusal of a provisional rule, a timeout: none of them is a reason to post.
            return (null, $"TaxCalculator.treatment failed ({ex.GetType().Name}) for {request.Category} {request.ChargeType}: {Truncate(ex.Message)}");
        }
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

    /// <summary>BIL sub-ledger payables: IPT payable and IPT not yet due, levy payable, other tax payable (stamp).</summary>
    private static TaxCategory? SourceCategory(string account) => account switch
    {
        "LA-06" or "LA-27" => TaxCategory.Tax,
        "LA-07" => TaxCategory.Levy,
        "LA-26" => TaxCategory.Stamp,
        _ => null,
    };

    /// <summary>Book accounts of the authority payables: IPT payable and not yet due, levy payable, stamp duty on the levy.</summary>
    private static TaxCategory? ResolvedCategory(string account) => account switch
    {
        "GL-2410" or "GL-2411" => TaxCategory.Tax,
        "GL-2420" => TaxCategory.Levy,
        "GL-2425" => TaxCategory.Stamp,
        _ => null,
    };

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200];
}
