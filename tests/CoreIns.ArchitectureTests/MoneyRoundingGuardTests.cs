using System.Text.RegularExpressions;

namespace CoreIns.ArchitectureTests;

/// <summary>
/// NX-EC-01: money rounding goes through one policy (<c>CoreIns.Rules.DecimalRounding</c>: <c>ToMidpoint</c>/<c>RoundTo</c>, fed by the
/// MKT rule). Under <c>src/CoreIns.Modules.*</c> nobody may (a) spell a literal commercial mode (<c>AwayFromZero</c>, <c>ToEven</c>,
/// <c>ToPositiveInfinity</c>, <c>ToNegativeInfinity</c>) or (b) call <c>decimal.Round</c>/<c>Math.Round</c> without any mode (silent
/// banker's rounding). <c>ToZero</c> is allowed: it is truncation (exact division, "is it already rounded" checks), not a policy.
/// Existing offenders are allow-listed per file with their count; NX-EC-02 migrates them and empties <see cref="AllowedForEc02"/>.
/// </summary>
public sealed partial class MoneyRoundingGuardTests
{
    /// <summary>file (relative to the repository root, forward slashes) to the number of offending calls. TODO(NX-EC-02): empty this.</summary>
    private static readonly Dictionary<string, int> AllowedForEc02 = new(StringComparer.Ordinal)
    {
        ["src/CoreIns.Modules.Claims/Commands/LiabilityFacts.cs"] = 1,
        ["src/CoreIns.Modules.Claims/Domain/Financials.cs"] = 3,
        ["src/CoreIns.Modules.Claims/Services/RecoveryEvidenceSource.cs"] = 1,
        ["src/CoreIns.Modules.Finance/Queries/JournalReader.cs"] = 1,
        ["src/CoreIns.Modules.Policy/Commands/Cancellation/CancelPolicy.cs"] = 2,
        ["src/CoreIns.Modules.Policy/Commands/Change/ChangeServicing.cs"] = 1,
        ["src/CoreIns.Modules.Policy/Commands/Change/RatingServicingTaxAdapter.cs"] = 1,
        ["src/CoreIns.Modules.Rating/Domain/RatingFacts.cs"] = 1,
        ["src/CoreIns.Modules.Underwriting/Domain/RuleSets.cs"] = 2,
    };

    [GeneratedRegex(@"\b(?:decimal|Math)\.Round\(")]
    private static partial Regex RoundCall();

    [GeneratedRegex(@"MidpointRounding\.(?:AwayFromZero|ToEven|ToPositiveInfinity|ToNegativeInfinity)")]
    private static partial Regex LiteralMode();

    [Fact]
    public void Money_modules_use_the_shared_rounding_policy()
    {
        var found = Offenders();
        var unexpected = found.Where(f => !AllowedForEc02.TryGetValue(f.Key, out var n) || n != f.Value.Count)
            .Select(f => $"{f.Key}: {string.Join("; ", f.Value)}").ToList();
        unexpected.ShouldBeEmpty("route rounding through CoreIns.Rules.DecimalRounding (ToMidpoint / RoundTo) with the MKT rule's mode");
    }

    [Fact]
    public void The_allow_list_holds_no_stale_entries()
    {
        var found = Offenders();
        AllowedForEc02.Keys.Where(k => !found.ContainsKey(k)).ShouldBeEmpty("remove migrated files from the EC-02 allow-list");
    }

    [Theory]
    [InlineData("var x = decimal.Round(a, 2);", 1)]
    [InlineData("var x = Math.Round(a);", 1)]
    [InlineData("var x = decimal.Round(a, 2, MidpointRounding.AwayFromZero);", 1)]
    [InlineData("var x = decimal.Round(a, 2, MidpointRounding.ToEven);", 1)]
    [InlineData("var x = decimal.Round(a, 2, MidpointRounding.ToZero);", 0)]
    [InlineData("var x = m.RoundToMinorUnits(MidpointRounding.AwayFromZero);", 1)]
    [InlineData("var x = decimal.Round(a, 2, DecimalRounding.ToMidpoint(m, a));", 0)]
    [InlineData("var x = decimal.Round(Calc(a, 3), 2, mode);", 0)]
    [InlineData("var x = decimal.Round(Calc(a, 3), 2);", 1)]
    public void The_scanner_flags_literal_and_default_rounding(string code, int expected) =>
        Scan(code).Count.ShouldBe(expected);

    private static SortedDictionary<string, List<string>> Offenders()
    {
        var root = SolutionModel.RepositoryRoot;
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var module in SolutionModel.ModuleNames)
        {
            var directory = Path.Combine(root, "src", SolutionModel.Implementation(module));
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                var hits = Scan(File.ReadAllText(file));
                if (hits.Count > 0)
                {
                    result[relative] = hits;
                }
            }
        }

        return result;
    }

    /// <summary>The offending calls in a source text (comments and string literals are not scanned for calls).</summary>
    private static List<string> Scan(string source)
    {
        var code = string.Join('\n', source.Split('\n').Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));
        var hits = LiteralMode().Matches(code).Select(m => m.Value).ToList();
        foreach (Match call in RoundCall().Matches(code))
        {
            var arguments = Arguments(code, call.Index + call.Length);
            if (!arguments.Contains("Midpoint", StringComparison.Ordinal) && !HasModeArgument(arguments))
            {
                hits.Add(call.Value + arguments.Trim() + ")");
            }
        }

        return hits;
    }

    /// <summary>The text up to the closing parenthesis that matches the call's opening one.</summary>
    private static string Arguments(string code, int start)
    {
        var depth = 1;
        var i = start;
        while (i < code.Length && depth > 0)
        {
            depth += code[i] == '(' ? 1 : code[i] == ')' ? -1 : 0;
            i++;
        }

        return code[start..Math.Max(start, i - 1)];
    }

    /// <summary>Whether the top-level argument list has a third argument (a mode variable, e.g. <c>mode</c>).</summary>
    private static bool HasModeArgument(string arguments)
    {
        var depth = 0;
        var commas = 0;
        foreach (var c in arguments)
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            commas += c == ',' && depth == 0 ? 1 : 0;
        }

        return commas >= 2;
    }
}
