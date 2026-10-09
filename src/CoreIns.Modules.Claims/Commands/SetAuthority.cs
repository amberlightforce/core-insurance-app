using CoreIns.Modules.Claims.Authority;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>One authority dimension set CLM must check for a transaction set (D-SL2-13, REQ-CLM-108).</summary>
/// <param name="Type">CLM.RESERVE or CLM.PAYMENT.</param>
/// <param name="CostType">Cost type code.</param>
/// <param name="Amount">The amount the authority is checked on.</param>
/// <param name="Currency">Currency (EUR in the slice).</param>
/// <param name="Basis">What the amount is: EXPOSURE_TOTAL_RESERVE, RESERVE_DECREASE, PAYMENT, CLAIM_CUMULATIVE_PAID.</param>
internal sealed record AuthorityRequirement(AuthorityTypeCode Type, string CostType, decimal Amount, string Currency, string Basis)
{
    /// <summary>The (type, cost type) one PLT approval covers; within it a larger amount dominates a smaller one (the grants are monotone in the amount).</summary>
    public (string Type, string CostType) Bucket => (Type.Value, CostType);
}

/// <summary>
/// The authority a set needs (D-SL2-13, superseding D-SL2-03's per-transaction wording; PRD-07 REQ-CLM-108):
/// <list type="bullet">
/// <item><b>CLM.RESERVE</b> on the resulting exposure total reserve (Σ approved reserves of the exposure + every reserve
/// transaction of the set on it, netted), for every exposure the set increases, per cost type it increases;</item>
/// <item>a manual reserve decrease on its absolute amount; the release a FINAL payment proposes in the same set is covered by
/// the payment and needs no check;</item>
/// <item><b>CLM.PAYMENT</b> per payment and on the claim's cumulative paid including the new payment.</item>
/// </list>
/// Requirements are pure functions of the set's immutable content and the approved balances, so CLM recomputes them at
/// approval and compares them with what the checkers were authorised for.
/// </summary>
internal static class SetAuthority
{
    public static IReadOnlyList<AuthorityRequirement> Requirements(
        SetContent content, IReadOnlyList<ReserveLineRow> claimLines, IReadOnlyDictionary<ReserveLineId, LineAmounts> approved)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new List<AuthorityRequirement>();
        var reserve = Codes.Of(TransactionKind.Reserve);
        var payment = Codes.Of(TransactionKind.Payment);
        var recoveryReserve = Codes.Of(TransactionKind.RecoveryReserve);
        string CostTypeOf(FinancialTransactionRow t) => content.Lines[t.ReserveLineId].CostType;

        foreach (var exposure in content.Transactions.Where(t => t.Kind == reserve).GroupBy(t => t.ExposureId))
        {
            var net = exposure.Sum(t => t.Amount);
            if (net <= 0m)
            {
                continue;
            }

            var total = claimLines.Where(l => l.ExposureId == exposure.Key).Sum(l => approved.GetValueOrDefault(l.ReserveLineId).Reserved) + net;
            foreach (var costType in exposure.GroupBy(CostTypeOf).Where(g => g.Sum(t => t.Amount) > 0m))
            {
                result.Add(new AuthorityRequirement(ClaimsAuthorityTypes.Reserve, costType.Key, total, costType.First().Currency, "EXPOSURE_TOTAL_RESERVE"));
            }
        }

        foreach (var decrease in content.Transactions.Where(t => t.Kind == reserve && t.Amount < 0m && !t.Proposed))
        {
            result.Add(new AuthorityRequirement(ClaimsAuthorityTypes.Reserve, CostTypeOf(decrease), -decrease.Amount, decrease.Currency, "RESERVE_DECREASE"));
        }

        foreach (var exposure in content.Transactions.Where(t => t.Kind == recoveryReserve).GroupBy(t => t.ExposureId))
        {
            var total = claimLines.Where(l => l.ExposureId == exposure.Key).Sum(l => approved.GetValueOrDefault(l.ReserveLineId).OpenRecoveryReserve)
                + exposure.Sum(t => t.Amount);
            foreach (var costType in exposure.GroupBy(CostTypeOf).Where(g => g.Sum(t => t.Amount) > 0m))
            {
                result.Add(new AuthorityRequirement(ClaimsAuthorityTypes.Reserve, costType.Key, total, costType.First().Currency, "EXPOSURE_TOTAL_RECOVERY_RESERVE"));
            }

            foreach (var decrease in exposure.Where(t => t.Amount < 0m && !t.Proposed))
            {
                result.Add(new AuthorityRequirement(ClaimsAuthorityTypes.Reserve, CostTypeOf(decrease), -decrease.Amount, decrease.Currency, "RESERVE_DECREASE"));
            }
        }

        var paidBefore = claimLines.Sum(l => approved.GetValueOrDefault(l.ReserveLineId).Paid);
        var paidInSet = content.Transactions.Where(t => t.Kind == payment).Sum(t => t.Amount);
        foreach (var p in content.Transactions.Where(t => t.Kind == payment))
        {
            result.Add(new AuthorityRequirement(ClaimsAuthorityTypes.Payment, CostTypeOf(p), p.Amount, p.Currency, "PAYMENT"));
            result.Add(new AuthorityRequirement(ClaimsAuthorityTypes.Payment, CostTypeOf(p), paidBefore + paidInSet, p.Currency, "CLAIM_CUMULATIVE_PAID"));
        }

        return [.. result.DistinctBy(r => (r.Type.Value, r.CostType, r.Amount))];
    }

    /// <summary>The largest amount per (type, cost type) among <paramref name="requirements"/>: what one approval must cover.</summary>
    public static IReadOnlyList<AuthorityRequirement> Dominant(IEnumerable<AuthorityRequirement> requirements) =>
        [.. requirements.GroupBy(r => r.Bucket).Select(g => g.MaxBy(r => r.Amount)!).OrderByDescending(r => r.Type == ClaimsAuthorityTypes.Payment).ThenBy(r => r.CostType, StringComparer.Ordinal)];

    public static Dictionary<string, DimensionValue> Dimensions(AuthorityRequirement requirement) => new(StringComparer.Ordinal)
    {
        [ClaimsAuthorityTypes.AmountDimension] = DimensionValue.Of(ClaimMoney.Of(requirement.Amount, requirement.Currency)),
        [ClaimsAuthorityTypes.CostTypeDimension] = DimensionValue.OfCodes(requirement.CostType),
    };

    public static Money MoneyOf(AuthorityRequirement requirement) => ClaimMoney.Of(requirement.Amount, requirement.Currency);

    public static async Task<AuthorityCheckResult> CheckAsync(AuthorityRequirement requirement, TransactionSetRow set,
        RequestContext context, IAuthorityService authority, Instant now, CancellationToken cancellationToken)
    {
        // A system principal has no standing money authority. Determine human eligibility against the manager's
        // configured ceiling, then refer for every eligible reserve/payment; a configured ceiling never self-approves it.
        var check = await authority.CheckAsync(new AuthorityCheckRequest(context.Actor,
            set.EvidenceRef is null ? context.Roles : ["Staff.ClaimsManager"], requirement.Type, Dimensions(requirement),
            ClaimApprovals.SetSubject(set.SetId), now), cancellationToken).ConfigureAwait(false);
        check = RequireFourEyes(requirement, check);
        return set.EvidenceRef is not null && check.Decision == AuthorityDecision.Allow
            ? check with { Decision = AuthorityDecision.Refer, ReasonCode = "SYSTEM_SET_REQUIRES_APPROVER", ReferralTargets = [new ReferralTarget("ROLE", "Staff.ClaimsManager")] }
            : check;
    }

    // BR-CLM-010: even a manager's own authority cannot remove four-eyes for a large-loss set.
    public static AuthorityCheckResult RequireFourEyes(AuthorityRequirement requirement, AuthorityCheckResult check) =>
        check.Decision == AuthorityDecision.Allow && requirement.Amount > 50000m
            ? check with { Decision = AuthorityDecision.Refer, ReasonCode = "FOUR_EYES_REQUIRED", ReferralTargets = [new ReferralTarget("ROLE", "Staff.ClaimsManager")] }
            : check;
}
