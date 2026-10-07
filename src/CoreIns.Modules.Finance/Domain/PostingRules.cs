using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Finance.Domain;

/// <summary>
/// One posting rule (REQ-FIN-048): the key is the posting source (registry name), the entry type of the source fact and
/// the source sub-ledger account, with the optional qualifiers charge category and charge type. The rule names the book
/// account, fixed (<see cref="Account"/>) or derived from the charge type's PFC GL key (<see cref="DeriveFrom"/>,
/// REQ-FIN-050). Side and amount come from the source line, so a balanced source entry gives a balanced journal.
/// </summary>
internal sealed record PostingRule(
    string Code,
    string SourceEvent,
    string EntryType,
    string SourceAccount,
    string? ChargeCategory,
    string? ChargeType,
    string? Account,
    string? DeriveFrom,
    string DescriptionEl,
    string DescriptionEn)
{
    /// <summary>Number of qualifiers: the most specific matching rule wins (REQ-FIN-049).</summary>
    public int Specificity => (ChargeCategory is null ? 0 : 1) + (ChargeType is null ? 0 : 1);

    /// <summary>True when the rule applies to a line with this category and charge type.</summary>
    public bool Matches(string sourceEvent, string entryType, string sourceAccount, string? chargeCategory, string? chargeType) =>
        string.Equals(SourceEvent, sourceEvent, StringComparison.Ordinal)
        && string.Equals(EntryType, entryType, StringComparison.Ordinal)
        && string.Equals(SourceAccount, sourceAccount, StringComparison.Ordinal)
        && (ChargeCategory is null || string.Equals(ChargeCategory, chargeCategory, StringComparison.Ordinal))
        && (ChargeType is null || string.Equals(ChargeType, chargeType, StringComparison.Ordinal));
}

/// <summary>A rule-set version of one legal entity and book (REQ-FIN-052), effective from a date.</summary>
internal sealed record RuleSet(
    Guid RuleSetId, string LegalEntity, string Book, int Version, BusinessDate EffectiveFrom, Sha256Hash ContentHash, IReadOnlyList<PostingRule> Rules);

/// <summary>The outcome of resolving a rule: the rule, or the intake-exception reason.</summary>
internal sealed record RuleMatch(PostingRule? Rule, string? Reason, string? Detail)
{
    public static RuleMatch Of(PostingRule rule) => new(rule, null, null);

    public static RuleMatch Fail(string reason, string detail) => new(null, reason, detail);
}

/// <summary>Pure rule resolution and compile checks (no I/O).</summary>
internal static class PostingRules
{
    /// <summary>
    /// Resolves exactly one rule (REQ-FIN-049): among the matching rules, the most specific; a tie is ambiguous and the
    /// event becomes an intake exception (there is never a default account, PRD-09 §1.2 decision 2).
    /// </summary>
    public static RuleMatch Resolve(
        IEnumerable<PostingRule> rules, string sourceEvent, string entryType, string sourceAccount, string? chargeCategory, string? chargeType)
    {
        var matching = rules.Where(r => r.Matches(sourceEvent, entryType, sourceAccount, chargeCategory, chargeType)).ToList();
        if (matching.Count == 0)
        {
            return RuleMatch.Fail(ExceptionReasons.NoRule,
                $"No posting rule for {sourceEvent} {entryType} line on {sourceAccount} (category {chargeCategory ?? "-"}, charge type {chargeType ?? "-"}).");
        }

        var best = matching.Max(r => r.Specificity);
        var top = matching.Where(r => r.Specificity == best).ToList();
        return top.Count == 1
            ? RuleMatch.Of(top[0])
            : RuleMatch.Fail(ExceptionReasons.AmbiguousRule, $"Rules {string.Join(", ", top.Select(r => r.Code))} match with the same specificity.");
    }

    /// <summary>
    /// Compile checks of a rule set (REQ-FIN-053 subset): unique codes; exactly one of account and derivation; fixed
    /// accounts exist in the chart; derivation targets exist in the chart; no two rules can tie for one line (same key,
    /// same specificity and compatible qualifiers).
    /// </summary>
    public static IReadOnlyList<string> Compile(
        IReadOnlyList<PostingRule> rules, IReadOnlySet<string> chartAccounts, IReadOnlyDictionary<string, string> derivations)
    {
        var errors = new List<string>();
        foreach (var duplicate in rules.GroupBy(r => r.Code, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"Rule code {duplicate.Key} is used more than once.");
        }

        foreach (var rule in rules)
        {
            if ((rule.Account is null) == (rule.DeriveFrom is null))
            {
                errors.Add($"Rule {rule.Code} must name exactly one of a fixed account and a derivation.");
            }

            if (rule.Account is not null && !chartAccounts.Contains(rule.Account))
            {
                errors.Add($"Rule {rule.Code} names account {rule.Account}, which is not in the chart.");
            }

            if (rule.DeriveFrom is not null && rule.DeriveFrom != Domain.DeriveFrom.GlKey)
            {
                errors.Add($"Rule {rule.Code} derives from unknown source {rule.DeriveFrom}.");
            }
        }

        foreach (var (glKey, account) in derivations)
        {
            if (!chartAccounts.Contains(account))
            {
                errors.Add($"GL key {glKey} derives account {account}, which is not in the chart.");
            }
        }

        for (var i = 0; i < rules.Count; i++)
        {
            for (var j = i + 1; j < rules.Count; j++)
            {
                var (a, b) = (rules[i], rules[j]);
                if (a.SourceEvent == b.SourceEvent && a.EntryType == b.EntryType && a.SourceAccount == b.SourceAccount
                    && a.Specificity == b.Specificity && Compatible(a.ChargeCategory, b.ChargeCategory) && Compatible(a.ChargeType, b.ChargeType))
                {
                    errors.Add($"Rules {a.Code} and {b.Code} can both match one line with the same specificity.");
                }
            }
        }

        return errors;
    }

    private static bool Compatible(string? left, string? right) => left is null || right is null || left == right;
}
