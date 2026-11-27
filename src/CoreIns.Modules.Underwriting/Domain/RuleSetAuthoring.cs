using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Rules;
using CoreIns.Rules.DecisionTables;

namespace CoreIns.Modules.Underwriting.Domain;

/// <summary>One finding of <see cref="RuleSetAnalysis"/>.</summary>
internal sealed record AnalysisIssue(string Code, string Message, string? RuleId = null, string? TestCase = null);

/// <summary>One test case outcome of <see cref="RuleSetAnalysis"/>.</summary>
internal sealed record AnalysisTestOutcome(string Name, bool Passed, string? Failure, IReadOnlyList<string> MatchedRuleIds);

/// <summary>
/// The full validation of a rule-set definition for authoring (REQ-UW-036, -038, -040): every finding at once instead of the first
/// one, which is what <see cref="CompiledRuleSet.Compile"/> (the activation gate) reports. A version is <see cref="Valid"/>, and so
/// may be submitted, when it compiles, has rules, every rule is expected to hit in a passing test case and the texts are translated.
/// </summary>
internal sealed class RuleSetAnalysis
{
    public const string CodeValidation = "UW-ERR-VALIDATION";
    public const string CodeUntested = "UW-ERR-RULE-UNTESTED";
    public const string CodeTranslation = "UW-ERR-TRANSLATION-MISSING";
    public const string CodeCompile = "UW-ERR-RULESET-COMPILE";

    private static readonly string[] RequiredOutputs = ["ruleType", "issueType", "severity", "blockingPoint", "messageEn", "messageEl"];

    private RuleSetAnalysis()
    {
    }

    public List<AnalysisIssue> Issues { get; } = [];

    public List<AnalysisTestOutcome> TestResults { get; } = [];

    public List<string> UncoveredRuleIds { get; } = [];

    public bool Compiles { get; private set; }

    public string? ContentHash { get; private set; }

    public bool Valid => Compiles && Issues.Count == 0;

    public static RuleSetAnalysis Analyse(RuleSetDto dto)
    {
        var analysis = new RuleSetAnalysis();
        analysis.Structure(dto);
        CompiledDecisionTable? table = null;
        try
        {
            table = CompiledRuleSet.BuildTable(dto);
        }
        catch (RuleCompileException ex)
        {
            foreach (var error in ex.Errors)
            {
                analysis.Issues.Add(new AnalysisIssue(CodeCompile, error.ToString()));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or DomainException or OverflowException)
        {
            analysis.Issues.Add(new AnalysisIssue(CodeCompile, ex is DomainException de ? de.Error.Detail ?? ex.Message : ex.Message));
        }

        if (table is null)
        {
            return analysis;
        }

        analysis.Compiles = true;
        analysis.ContentHash = table.ContentHash;
        var cases = new List<DecisionTableTestCase>();
        foreach (var testCase in dto.TestCases)
        {
            try
            {
                cases.Add(CompiledRuleSet.BuildCase(testCase));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                analysis.Issues.Add(new AnalysisIssue(CodeValidation, $"Test case '{testCase.Name}' is malformed: {ex.Message}", TestCase: testCase.Name));
            }
        }

        var readiness = table.CheckActivationReadiness(cases, requireEveryRuleCovered: true);
        foreach (var outcome in readiness.Outcomes)
        {
            analysis.TestResults.Add(new AnalysisTestOutcome(outcome.Name, outcome.Passed, outcome.Failure, [.. outcome.Result.MatchedRuleIds]));
            if (!outcome.Passed)
            {
                analysis.Issues.Add(new AnalysisIssue(CodeValidation, $"Test case '{outcome.Name}' fails: {outcome.Failure}", TestCase: outcome.Name));
            }
        }

        analysis.UncoveredRuleIds.AddRange(readiness.UncoveredRuleIds);
        foreach (var id in readiness.UncoveredRuleIds)
        {
            analysis.Issues.Add(new AnalysisIssue(CodeUntested, $"Rule {id} is not expected to hit in any test case.", RuleId: id));
        }

        if (dto.Rules.Count == 0)
        {
            analysis.Issues.Add(new AnalysisIssue(CodeValidation, "The rule set has no rules."));
        }

        return analysis;
    }

    private void Structure(RuleSetDto dto)
    {
        if (dto.Checkpoint is not ("PRE_QUOTE" or "PRE_BIND"))
        {
            Issues.Add(new AnalysisIssue(CodeValidation, "The checkpoint must be PRE_QUOTE or PRE_BIND."));
        }

        if (!DateOnly.TryParseExact(dto.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            Issues.Add(new AnalysisIssue(CodeValidation, "effectiveFrom must be a date, yyyy-MM-dd."));
        }

        if (!Enum.TryParse<HitPolicy>(dto.HitPolicy, ignoreCase: true, out _))
        {
            Issues.Add(new AnalysisIssue(CodeValidation, $"Unknown hit policy '{dto.HitPolicy}'."));
        }

        foreach (var required in RequiredOutputs.Where(r => dto.Outputs.All(o => o.Name != r)))
        {
            Issues.Add(new AnalysisIssue(CodeValidation, $"The output column '{required}' is required (the evaluation reads it)."));
        }

        foreach (var duplicate in dto.Rules.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            Issues.Add(new AnalysisIssue(CodeValidation, $"Rule id {duplicate.Key} is used more than once.", RuleId: duplicate.Key));
        }

        var en = dto.Outputs.FindIndex(o => o.Name == "messageEn");
        var el = dto.Outputs.FindIndex(o => o.Name == "messageEl");
        foreach (var rule in dto.Rules)
        {
            if (rule.Conditions.Count != dto.Inputs.Count)
            {
                Issues.Add(new AnalysisIssue(CodeValidation, $"Rule {rule.Id} has {rule.Conditions.Count} conditions; the table has {dto.Inputs.Count} input columns.", RuleId: rule.Id));
            }

            if (rule.Outputs.Count != dto.Outputs.Count)
            {
                Issues.Add(new AnalysisIssue(CodeValidation, $"Rule {rule.Id} has {rule.Outputs.Count} outputs; the table has {dto.Outputs.Count} output columns.", RuleId: rule.Id));
                continue;
            }

            foreach (var (index, language) in new[] { (en, "English"), (el, "Greek") })
            {
                if (index >= 0 && IsBlankText(rule.Outputs[index]))
                {
                    Issues.Add(new AnalysisIssue(CodeTranslation, $"Rule {rule.Id} has no {language} message.", RuleId: rule.Id));
                }
            }
        }
    }

    private static bool IsBlankText(string expression)
    {
        var text = expression.Trim();
        return text.Length == 0 || (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && text[1..^1].Trim().Length == 0);
    }
}

/// <summary>Authoring operations on <see cref="RuleSetDto"/>: skeleton, hashing, patches, mapping to and from the contract.</summary>
internal static class RuleSetAuthoring
{
    public const string ExportFormat = "coreins.uw.rule-set/1";

    private static readonly JsonSerializerOptions ContractOptions = new(JsonSerializerDefaults.Web);

    /// <summary>An empty table on the standard motor columns: no rules, no test cases (a Draft).</summary>
    public static RuleSetDto Skeleton(string code, string productCode, string checkpoint, string version, string effectiveFrom, string? note) => new(
        code, version, productCode, checkpoint, effectiveFrom, "ILLUSTRATIVE_TEST_DATA", note ?? string.Empty, "Collect",
        BuiltInRuleSets.StandardVariables(), BuiltInRuleSets.StandardInputs(), BuiltInRuleSets.StandardOutputs(), [], []);

    public static RuleSetDto Clone(RuleSetDto dto) => RuleSetJson.Deserialize(RuleSetJson.Serialize(dto));

    /// <summary>
    /// SHA-256 (lower-case hex) of the definition without its version number and data status: the same rules and test cases hash the
    /// same after an export and an import, whatever version they get (REQ-UW-032).
    /// </summary>
    public static string DefinitionHash(RuleSetDto dto)
    {
        var normal = dto with { Version = string.Empty, DataStatus = string.Empty };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RuleSetJson.Serialize(normal))));
    }

    /// <summary>The next version number of a code: the highest major plus one, as "N.0".</summary>
    public static string NextVersion(IEnumerable<string> existing)
    {
        var max = 0;
        foreach (var version in existing)
        {
            var head = version.Split('.')[0];
            if (int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
            {
                max = Math.Max(max, major);
            }
        }

        return (max + 1).ToString(CultureInfo.InvariantCulture) + ".0";
    }

    /// <summary>Applies the patches in order to a copy; throws a UW validation <see cref="DomainException"/> for a patch that cannot be applied.</summary>
    public static RuleSetDto Apply(RuleSetDto dto, IReadOnlyList<RuleSetEditPatch> patches)
    {
        var next = Clone(dto);
        foreach (var patch in patches)
        {
            switch (patch.Op)
            {
                case RuleSetEditPatch.OpValue.SetCell:
                    SetCell(next, Need(patch.RuleId, "ruleId"), Need(patch.Column, "column"), patch.Value ?? throw Invalid("value is required for SET_CELL."));
                    break;
                case RuleSetEditPatch.OpValue.AddRule:
                    var rule = patch.Rule ?? throw Invalid("rule is required for ADD_RULE.");
                    if (string.IsNullOrWhiteSpace(rule.Id) || next.Rules.Any(r => r.Id == rule.Id))
                    {
                        throw Invalid($"Rule id '{rule.Id}' is empty or already used.");
                    }

                    next.Rules.Add(new RuleRowDto(rule.Id, [.. rule.Conditions], [.. rule.Outputs], rule.Explain is null ? null : new RuleExplainDto(rule.Explain.Fact, rule.Explain.Limit)));
                    break;
                case RuleSetEditPatch.OpValue.RemoveRule:
                    var removed = Need(patch.RuleId, "ruleId");
                    if (next.Rules.RemoveAll(r => r.Id == removed) == 0)
                    {
                        throw Invalid($"Rule '{removed}' does not exist.");
                    }

                    break;
                case RuleSetEditPatch.OpValue.AddTestCase:
                    var testCase = patch.TestCase ?? throw Invalid("testCase is required for ADD_TEST_CASE.");
                    if (string.IsNullOrWhiteSpace(testCase.Name) || next.TestCases.Any(t => t.Name == testCase.Name))
                    {
                        throw Invalid($"Test case name '{testCase.Name}' is empty or already used.");
                    }

                    next.TestCases.Add(ToDto(testCase));
                    break;
                case RuleSetEditPatch.OpValue.RemoveTestCase:
                    var name = Need(patch.TestCaseName, "testCaseName");
                    if (next.TestCases.RemoveAll(t => t.Name == name) == 0)
                    {
                        throw Invalid($"Test case '{name}' does not exist.");
                    }

                    break;
                case RuleSetEditPatch.OpValue.SetMeta:
                    if (patch.EffectiveFrom is not null)
                    {
                        if (!DateOnly.TryParseExact(patch.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        {
                            throw Invalid("effectiveFrom must be a date, yyyy-MM-dd.");
                        }

                        next = next with { EffectiveFrom = patch.EffectiveFrom };
                    }

                    if (patch.Note is not null)
                    {
                        next = next with { Note = patch.Note };
                    }

                    break;
                default:
                    throw Invalid("Unknown patch operation.");
            }
        }

        return next;
    }

    private static void SetCell(RuleSetDto dto, string ruleId, string column, string value)
    {
        var index = dto.Rules.FindIndex(r => r.Id == ruleId);
        if (index < 0)
        {
            throw Invalid($"Rule '{ruleId}' does not exist.");
        }

        var rule = dto.Rules[index];
        var input = dto.Inputs.FindIndex(c => c.Name == column);
        var output = dto.Outputs.FindIndex(c => c.Name == column);
        if (input >= 0 && input < rule.Conditions.Count)
        {
            rule.Conditions[input] = value;
        }
        else if (output >= 0 && output < rule.Outputs.Count)
        {
            rule.Outputs[output] = value;
        }
        else
        {
            throw Invalid($"Column '{column}' is not a column of the table (or the rule has no cell for it).");
        }
    }

    private static string Need(string? value, string field) => string.IsNullOrWhiteSpace(value) ? throw Invalid($"{field} is required.") : value;

    public static DomainException Invalid(string message) => new(DomainError.Of(ModuleCode.UW, "VALIDATION", message));

    public static RuleTestCaseDto ToDto(RuleSetTestCase c) => new(
        c.Name, c.FirstRegistrationYear, c.VehicleValue, c.EngineCc, c.Usage, [.. c.DriverBirthDates], c.ClaimsLast5Years, c.EffectiveDate, [.. c.ExpectedRuleIds]);

    /// <summary>Contract definition to the stored form (same member names: a JSON round trip).</summary>
    public static RuleSetDto FromContract(RuleSetDefinition definition) =>
        JsonSerializer.Deserialize<RuleSetDto>(JsonSerializer.Serialize(definition, ContractOptions), RuleSetJson.Options)!;

    /// <summary>Stored form to the contract definition.</summary>
    public static RuleSetDefinition ToContract(RuleSetDto dto) =>
        JsonSerializer.Deserialize<RuleSetDefinition>(RuleSetJson.Serialize(dto), ContractOptions)!;

    public static RuleSetValidationReport ToReport(string code, string version, RuleSetAnalysis analysis) => new()
    {
        RuleSetCode = code,
        Version = version,
        Valid = analysis.Valid,
        Compiles = analysis.Compiles,
        Issues = [.. analysis.Issues.Select(i => new RuleSetValidationIssue { Code = i.Code, Message = i.Message, RuleId = i.RuleId, TestCase = i.TestCase })],
        TestResults = [.. analysis.TestResults.Select(t => new RuleSetTestOutcome { Name = t.Name, Passed = t.Passed, Failure = t.Failure, MatchedRuleIds = t.MatchedRuleIds })],
        UncoveredRuleIds = analysis.UncoveredRuleIds,
        ContentHash = analysis.ContentHash,
    };
}
