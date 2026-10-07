using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

/// <summary>
/// CEL-subset conformance suite for language version 1.0 (REQ-PLT-364): every adopted operator, macro and function has
/// conformance cases, results follow CEL semantics (with the documented decimal deviation), and anything outside the
/// adopted subset fails compilation.
/// </summary>
public class ConformanceTests
{
    /// <summary>(feature, expression, expected display value). Expected values follow the CEL specification.</summary>
    public static readonly IReadOnlyList<(string Feature, string Expression, string Expected)> Cases = new[]
    {
        ("_?_:_", "true ? 1 : 2", "1"),
        ("_||_", "false || true", "true"),
        ("_&&_", "true && false", "false"),
        ("!_", "!true", "false"),
        ("-_", "-(3)", "-3"),
        ("_+_", "1 + 2", "3"),
        ("_+_", "'ab' + 'cd'", "\"abcd\""),
        ("_+_", "[1] + [2]", "[1, 2]"),
        ("_-_", "5 - 7", "-2"),
        ("_*_", "6 * 7", "42"),
        ("_/_", "7 / 2", "3"),
        ("_/_", "-7 / 2", "-3"),
        ("_%_", "7 % 3", "1"),
        ("_%_", "-7 % 3", "-1"),
        ("_==_", "1 == 1", "true"),
        ("_==_", "[1, 'a'.size()] == [1, 1]", "true"),
        ("_!=_", "'a' != 'b'", "true"),
        ("_<_", "1 < 2", "true"),
        ("_<=_", "2 <= 2", "true"),
        ("_>_", "'b' > 'a'", "true"),
        ("_>=_", "1 >= 2", "false"),
        ("@in", "2 in [1, 2, 3]", "true"),
        ("@in", "'k' in {'k': 1}", "true"),
        ("_[_]", "[10, 20][1]", "20"),
        ("_[_]", "{'a': 'x'}['a']", "\"x\""),
        ("has", "has({'a': 1}.a)", "true"),
        ("all", "[1, 2, 3].all(x, x > 0)", "true"),
        ("exists", "[1, 2, 3].exists(x, x == 2)", "true"),
        ("exists_one", "[1, 2, 3].exists_one(x, x > 2)", "true"),
        ("map", "[1, 2, 3].map(x, x * x)", "[1, 4, 9]"),
        ("map", "[1, 2, 3].map(x, x % 2 == 1, x * 10)", "[10, 30]"),
        ("filter", "[1, 2, 3].filter(x, x != 2)", "[1, 3]"),
        ("size", "size('héllo')", "5"),
        ("size", "'abc'.size()", "3"),
        ("size", "size([1, 2])", "2"),
        ("size", "size({'a': 1})", "1"),
        ("int", "int('42')", "42"),
        ("int", "int(2.9)", "2"),
        ("decimal", "decimal(3)", "3"),
        ("decimal", "decimal('0.10')", "0.10"),
        ("string", "string(12)", "\"12\""),
        ("date", "date('2026-11-01')", "date(\"2026-11-01\")"),
        ("timestamp", "timestamp('2026-11-01T10:00:00Z')", "timestamp(\"2026-11-01T10:00:00Z\")"),
        ("duration", "duration('1m')", "duration(\"60s\")"),
        ("round", "round(15.015, 2, 'HalfUp')", "15.02"),
        ("round", "round(0.125, 2, 'HalfEven')", "0.12"),
        ("abs", "abs(-4)", "4"),
        ("min", "min(4, 2, 9)", "2"),
        ("max", "max([4, 2, 9])", "9"),
        ("sum", "sum([1.10, 2.20])", "3.30"),
        ("ageAt", "ageAt(date('2009-01-15'), date('2026-11-01'))", "17"),
        ("yearsBetween", "yearsBetween(date('2020-02-29'), date('2021-02-28'))", "1"),
        ("monthsBetween", "monthsBetween(date('2026-01-15'), date('2026-04-14'))", "2"),
        ("daysBetween", "daysBetween(date('2026-11-01'), date('2026-12-01'))", "30"),
        ("addDays", "addDays(date('2026-12-31'), 1)", "date(\"2027-01-01\")"),
        ("addMonths", "addMonths(date('2026-08-31'), 1)", "date(\"2026-09-30\")"),
        ("addYears", "addYears(date('2026-11-01'), 1)", "date(\"2027-11-01\")"),
        ("year", "year(date('2026-11-01'))", "2026"),
        ("month", "month(date('2026-11-01'))", "11"),
        ("day", "day(date('2026-11-01'))", "1"),
        ("dayOfWeek", "dayOfWeek(date('2026-11-04'))", "3"),
        ("matches", "matches('AB12', '^[A-Z]+[0-9]+$')", "true"),
        ("matches", "'AB12'.matches('[0-9]{3}')", "false"),
        ("startsWith", "'motor'.startsWith('mo')", "true"),
        ("endsWith", "'motor'.endsWith('or')", "true"),
        ("contains", "'motor'.contains('oto')", "true"),
        ("lowerAscii", "'MoToR'.lowerAscii()", "\"motor\""),
        ("upperAscii", "'MoToR'.upperAscii()", "\"MOTOR\""),
        ("trim", "' motor '.trim()", "\"motor\""),
    };

    private static readonly RuleEnvironment Env = RuleEnvironment.Create(InputSchema.Define().Build());

    public static IEnumerable<object[]> CaseData => Cases.Select(c => new object[] { c.Feature, c.Expression, c.Expected });

    [Theory]
    [MemberData(nameof(CaseData))]
    public void Conformance_case(string feature, string expression, string expected)
    {
        var compiled = Env.Compile(expression);
        compiled.Evaluate(Env.Schema.NewInputs().Build()).GetValueOrThrow().ToString().ShouldBe(expected, feature);
    }

    [Fact]
    public void Every_adopted_feature_has_conformance_cases()
    {
        var adopted = RuleLanguage.Operators.Concat(RuleLanguage.Macros).Concat(RuleLanguage.GlobalFunctions).Concat(RuleLanguage.MemberFunctions).Distinct();
        var covered = Cases.Select(c => c.Feature).ToHashSet(StringComparer.Ordinal);
        adopted.Where(f => !covered.Contains(f)).ShouldBeEmpty();
        covered.Where(f => !adopted.Contains(f)).ShouldBeEmpty();
        RuleLanguage.Version.ShouldBe("1.0");
        RuleLanguage.RoundingModes.ShouldBe(new[] { "HalfEven", "HalfUp", "HalfDown", "Up", "Down", "Ceiling", "Floor" });
    }

    [Theory]
    [InlineData("double(1)")]
    [InlineData("uint(1)")]
    [InlineData("bytes('a')")]
    [InlineData("dyn(1)")]
    [InlineData("type(1)")]
    [InlineData("timestamp('2026-01-01T00:00:00Z').getFullYear()")]
    [InlineData("duration('1h').getHours()")]
    [InlineData("math.greatest(1, 2)")]
    [InlineData("'a,b'.split(',')")]
    [InlineData("'abc'.substring(1)")]
    [InlineData("optional.of(1)")]
    [InlineData("[1].sort()")]
    [InlineData("now()")]
    [InlineData("1.0e3")]
    [InlineData("1u")]
    [InlineData("b'abc'")]
    [InlineData("google.protobuf.Timestamp{seconds: 1}")]
    [InlineData(".x")]
    public void Functions_outside_the_adopted_subset_fail_compilation(string expression)
    {
        var result = Env.TryCompile(expression);
        result.IsSuccess.ShouldBeFalse();
        result.Errors.Single().Code.ShouldBeOneOf(
            RuleErrorCode.UnsupportedFeature, RuleErrorCode.UnknownFunction, RuleErrorCode.NonDeterministic, RuleErrorCode.Syntax);
    }
}
