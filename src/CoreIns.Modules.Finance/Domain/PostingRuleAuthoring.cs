using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Finance.Domain;

/// <summary>Draft states of a posting-rule set proposal (BO-FIN-07). An approved draft becomes a new immutable rule-set version.</summary>
internal static class PostingRuleDraftStatus
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";
}

/// <summary>One rule row as the author sends it (the wire shape).</summary>
internal sealed record RuleRowInput(
    string? Code, string? SourceEvent, string? EntryType, string? SourceAccount, string? ChargeCategory, string? ChargeType,
    string? Account, string? DeriveFrom, string? DescriptionEl, string? DescriptionEn);

/// <summary>A sample (golden) event: the facts of one source line and what the author expects the rules to do with it.</summary>
internal sealed record SampleEventInput(
    string? Name, string? SourceEvent, string? EntryType, string? SourceAccount, string? ChargeCategory, string? ChargeType,
    string? GlKey, string? ExpectRule, string? ExpectAccount);

/// <summary>The rule set being authored: an existing draft, or the book and effective date of a new one.</summary>
internal sealed record RuleSetInput(Guid? DraftId, string? Book, string? EffectiveFrom);

/// <summary>Request of <c>draft</c>, <c>compile</c>, <c>test</c> and <c>previewImpact</c>; rows and samples default to the stored draft's.</summary>
internal sealed record PostingRulesAuthoringInput(RuleSetInput? RuleSet, IReadOnlyList<RuleRowInput>? RuleRows, IReadOnlyList<SampleEventInput>? SampleEvents, int? Limit);

/// <summary><c>fin.PostingRules.submit</c> request.</summary>
internal sealed record PostingRulesSubmitInput(Guid? DraftId);

/// <summary><c>fin.PostingRules.decide</c> request: <c>decision</c> is APPROVE or REJECT; a rejection needs a comment.</summary>
internal sealed record PostingRulesDecideInput(Guid? DraftId, string? Decision, string? Comment);

internal sealed record PostingRuleDraftView(
    Guid DraftId, string Book, string EffectiveFrom, string Status, int? BaseVersion, string ContentHash, int RuleCount, int RecordVersion,
    string CreatedBy, string? SubmittedBy, string? DecidedBy, string? DecisionReason, Guid? ActivatedRuleSetId, int? ActivatedVersion);

internal sealed record PostingRuleCompileResult(bool Ok, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

internal sealed record PostingRuleGoldenCase(string Name, bool Passed, string? RuleCode, string? Account, string? Detail);

internal sealed record PostingRuleGoldenResult(int Total, int Passed, int Failed, IReadOnlyList<PostingRuleGoldenCase> Cases);

/// <summary>One replayed line whose account or rule would change (or that would no longer resolve).</summary>
internal sealed record PostingRuleImpactLine(string JournalNumber, int LineNo, string Outcome, string OldRule, string? NewRule, string OldAccount, string? NewAccount, string? Detail);

internal sealed record PostingRuleImpactResult(int Journals, int Lines, int Unchanged, int Changed, int Unresolved, int Skipped, IReadOnlyList<PostingRuleImpactLine> Differences);

/// <summary>The envelope every authoring operation answers; the parts an operation does not produce are null.</summary>
internal sealed record PostingRulesAuthoringResult(
    PostingRuleDraftView? Draft, PostingRuleCompileResult? CompileResults, PostingRuleGoldenResult? GoldenResults, PostingRuleImpactResult? Preview);

/// <summary>A replayed journal line projected for <see cref="PostingRuleAuthoring.Replay"/>.</summary>
internal sealed record ReplayLine(
    string JournalNumber, int LineNo, PostingRule OriginalRule, string? ChargeCategory, string? ChargeType, string? GlKey, string Account);

/// <summary>Pure authoring logic (no I/O): shape checks, content hash, golden evaluation and replay. Compile checks are <see cref="PostingRules.Compile"/>.</summary>
internal static partial class PostingRuleAuthoring
{
    public const int MaxRules = 500;
    public const int MaxSamples = 200;
    public const int MaxDifferences = 100;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,99}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CodePattern();

    private static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.FIN, "VALIDATION", detail);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Shape checks of the rows; semantic checks (chart, ties, derivations) are the compile step so a draft can be saved while incomplete.</summary>
    public static Result<IReadOnlyList<PostingRule>> ParseRules(IReadOnlyList<RuleRowInput>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return Invalid("ruleRows must hold at least one rule.");
        }

        if (rows.Count > MaxRules)
        {
            return Invalid($"A rule set holds at most {MaxRules} rules.");
        }

        var rules = new List<PostingRule>(rows.Count);
        foreach (var row in rows)
        {
            if (row is null)
            {
                return Invalid("A rule row is null.");
            }

            var code = Clean(row.Code);
            var required = new[] { code, Clean(row.SourceEvent), Clean(row.EntryType), Clean(row.SourceAccount) };
            if (required.Any(v => v is null || !CodePattern().IsMatch(v)))
            {
                return Invalid($"Rule {code ?? "?"}: code, sourceEvent, entryType and sourceAccount are required (letters, digits, '.', '_' or '-', at most 100 characters).");
            }

            var optional = new[] { Clean(row.ChargeCategory), Clean(row.ChargeType), Clean(row.Account), Clean(row.DeriveFrom) };
            if (optional.Any(v => v is not null && !CodePattern().IsMatch(v)))
            {
                return Invalid($"Rule {code}: chargeCategory, chargeType, account and deriveFrom must be codes.");
            }

            var el = Clean(row.DescriptionEl) ?? code!;
            var en = Clean(row.DescriptionEn) ?? code!;
            if (el.Length > 200 || en.Length > 200)
            {
                return Invalid($"Rule {code}: descriptions are at most 200 characters.");
            }

            rules.Add(new PostingRule(code!, required[1]!, required[2]!, required[3]!, optional[0], optional[1], optional[2], optional[3], el, en));
        }

        return rules;
    }

    public static Result<IReadOnlyList<SampleEventInput>> ParseSamples(IReadOnlyList<SampleEventInput>? samples)
    {
        samples ??= [];
        if (samples.Count > MaxSamples)
        {
            return Invalid($"At most {MaxSamples} sample events are accepted.");
        }

        var clean = new List<SampleEventInput>(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s is null || Clean(s.SourceEvent) is not { } sourceEvent || Clean(s.EntryType) is not { } entryType || Clean(s.SourceAccount) is not { } account)
            {
                return Invalid($"Sample event {i + 1}: sourceEvent, entryType and sourceAccount are required.");
            }

            clean.Add(new SampleEventInput(
                Clean(s.Name) ?? $"sample-{i + 1}", sourceEvent, entryType, account, Clean(s.ChargeCategory), Clean(s.ChargeType), Clean(s.GlKey), Clean(s.ExpectRule), Clean(s.ExpectAccount)));
        }

        return clean;
    }

    public static RuleRowInput ToInput(PostingRule r) =>
        new(r.Code, r.SourceEvent, r.EntryType, r.SourceAccount, r.ChargeCategory, r.ChargeType, r.Account, r.DeriveFrom, r.DescriptionEl, r.DescriptionEn);

    /// <summary>The hash a checker approves: book, effective date and every rule, in code order. Anything that changes behaviour changes it.</summary>
    public static Sha256Hash Hash(string book, BusinessDate effectiveFrom, IEnumerable<PostingRule> rules)
    {
        var text = new StringBuilder("FIN.POSTING_RULE_SET/v1|").Append(book).Append('|').Append(effectiveFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        foreach (var r in rules.OrderBy(r => r.Code, StringComparer.Ordinal))
        {
            text.Append('|').Append(string.Join(';', r.Code, r.SourceEvent, r.EntryType, r.SourceAccount, r.ChargeCategory, r.ChargeType, r.Account, r.DeriveFrom, r.DescriptionEl, r.DescriptionEn));
        }

        return Sha256Hash.ComputeUtf8(text.ToString());
    }

    /// <summary>The hash stored on the rule-set version an approval creates (rules only: the version row carries the date).</summary>
    public static Sha256Hash RuleSetHash(string book, IEnumerable<PostingRule> rules) => Hash(book, new BusinessDate(1, 1, 1), rules);

    private static (string? Account, string? Problem) AccountOf(PostingRule rule, string? glKey, IReadOnlyDictionary<string, string> derivations)
    {
        if (rule.Account is not null)
        {
            return (rule.Account, null);
        }

        if (glKey is null)
        {
            return (null, "the rule derives its account from the GL key, but the event names none");
        }

        return derivations.TryGetValue(glKey, out var derived) ? (derived, null) : (null, $"GL key {glKey} derives no account");
    }

    /// <summary>Resolves every sample against the rules and compares with what the author expected (REQ-FIN-049).</summary>
    public static PostingRuleGoldenResult Golden(IReadOnlyList<PostingRule> rules, IReadOnlyDictionary<string, string> derivations, IReadOnlyList<SampleEventInput> samples)
    {
        var cases = new List<PostingRuleGoldenCase>(samples.Count);
        foreach (var s in samples)
        {
            var match = PostingRules.Resolve(rules, s.SourceEvent!, s.EntryType!, s.SourceAccount!, s.ChargeCategory, s.ChargeType);
            if (match.Rule is null)
            {
                // An author may expect "no rule" (the event must become an intake exception): ExpectRule NONE.
                var expectedNone = string.Equals(s.ExpectRule, "NONE", StringComparison.Ordinal);
                cases.Add(new PostingRuleGoldenCase(s.Name!, expectedNone, null, null, match.Detail));
                continue;
            }

            var (account, problem) = AccountOf(match.Rule, s.GlKey, derivations);
            var failures = new List<string>();
            if (problem is not null)
            {
                failures.Add(problem);
            }

            if (s.ExpectRule is not null && !string.Equals(s.ExpectRule, match.Rule.Code, StringComparison.Ordinal))
            {
                failures.Add($"expected rule {s.ExpectRule}, got {match.Rule.Code}");
            }

            if (s.ExpectAccount is not null && !string.Equals(s.ExpectAccount, account, StringComparison.Ordinal))
            {
                failures.Add($"expected account {s.ExpectAccount}, got {account ?? "none"}");
            }

            cases.Add(new PostingRuleGoldenCase(s.Name!, failures.Count == 0, match.Rule.Code, account, failures.Count == 0 ? null : string.Join("; ", failures)));
        }

        var passed = cases.Count(c => c.Passed);
        return new PostingRuleGoldenResult(cases.Count, passed, cases.Count - passed, cases);
    }

    /// <summary>
    /// Replays posted lines through the proposed rules without posting anything: each line keeps the key of the rule that posted it and its
    /// own charge qualifiers, and the proposed set decides which rule and account it would have got.
    /// </summary>
    public static PostingRuleImpactResult Replay(
        IReadOnlyList<PostingRule> rules, IReadOnlyDictionary<string, string> derivations, IReadOnlyList<ReplayLine> lines, int journals, int skipped)
    {
        var differences = new List<PostingRuleImpactLine>();
        int unchanged = 0, changed = 0, unresolved = 0;
        foreach (var line in lines)
        {
            var original = line.OriginalRule;
            var match = PostingRules.Resolve(rules, original.SourceEvent, original.EntryType, original.SourceAccount, line.ChargeCategory, line.ChargeType);
            if (match.Rule is null)
            {
                unresolved++;
                Add(new PostingRuleImpactLine(line.JournalNumber, line.LineNo, "UNRESOLVED", original.Code, null, line.Account, null, match.Detail));
                continue;
            }

            var (account, problem) = AccountOf(match.Rule, line.GlKey, derivations);
            if (account is null)
            {
                unresolved++;
                Add(new PostingRuleImpactLine(line.JournalNumber, line.LineNo, "UNRESOLVED", original.Code, match.Rule.Code, line.Account, null, problem));
            }
            else if (!string.Equals(account, line.Account, StringComparison.Ordinal))
            {
                changed++;
                Add(new PostingRuleImpactLine(line.JournalNumber, line.LineNo, "CHANGED", original.Code, match.Rule.Code, line.Account, account, null));
            }
            else
            {
                unchanged++;
            }
        }

        return new PostingRuleImpactResult(journals, lines.Count, unchanged, changed, unresolved, skipped, differences);

        void Add(PostingRuleImpactLine item)
        {
            if (differences.Count < MaxDifferences)
            {
                differences.Add(item);
            }
        }
    }
}
