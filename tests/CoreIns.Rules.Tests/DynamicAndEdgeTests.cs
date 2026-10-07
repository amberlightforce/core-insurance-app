using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

/// <summary>
/// Run-time checks behind statically dynamic values (a host function may return <c>dyn</c>), null handling, and
/// boundary cases of the value model.
/// </summary>
public class DynamicAndEdgeTests
{
    private static RuleValue Sample(string kind) => kind switch
    {
        "int" => 5,
        "dec" => 2.5m,
        "str" => "a",
        "bool" => true,
        "null" => RuleValue.Null,
        "list" => RuleValue.List(1, 2),
        "mixed" => RuleValue.List(1, "a"),
        "map" => RuleValue.Map(new[] { new KeyValuePair<RuleValue, RuleValue>("a", 1) }),
        "obj" => T.Default["vehicle"],
        "date" => new DateOnly(2026, 1, 1),
        "ts" => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        "dur" => new TimeSpan(0, 1, 0),
        _ => throw new ArgumentException(kind),
    };

    private static readonly HostFunction AnyOf = new("anyOf", new[] { RuleType.String }, RuleType.Dyn, args => Sample(((StringValue)args[0]).Value));

    private static readonly RuleEnvironment Env = RuleEnvironment.Create(T.Schema, functions: new[] { AnyOf });

    private static EvaluationResult Run(string source, RuleType? expected = null) =>
        Env.Compile(source, expected).Evaluate(T.Default, new EvaluationOptions { Trace = true });

    [Theory]
    [InlineData("anyOf('int') + 1", "6")]
    [InlineData("anyOf('dec') * 2", "5.0")]
    [InlineData("anyOf('int') - anyOf('dec')", "2.5")]
    [InlineData("anyOf('int') / 2", "2")]
    [InlineData("anyOf('int') % 2", "1")]
    [InlineData("-anyOf('dec')", "-2.5")]
    [InlineData("anyOf('str') + 'b'", "\"ab\"")]
    [InlineData("anyOf('list') + [3]", "[1, 2, 3]")]
    [InlineData("anyOf('int') > 4", "true")]
    [InlineData("anyOf('int') == 5.0", "true")]
    [InlineData("anyOf('bool') && true", "true")]
    [InlineData("anyOf('bool') ? 1 : 2", "1")]
    [InlineData("1 in anyOf('list')", "true")]
    [InlineData("'a' in anyOf('map')", "true")]
    [InlineData("anyOf('list')[1]", "2")]
    [InlineData("anyOf('map')['a']", "1")]
    [InlineData("anyOf('list').all(i, i > 0)", "true")]
    [InlineData("anyOf('map').exists(k, k == 'a')", "true")]
    [InlineData("anyOf('list').map(i, i * 2)", "[2, 4]")]
    [InlineData("size(anyOf('str'))", "1")]
    [InlineData("anyOf('date') < dt", "true")]
    [InlineData("anyOf('ts') + anyOf('dur')", "timestamp(\"2026-01-01T00:01:00Z\")")]
    [InlineData("anyOf('dur') + anyOf('ts')", "timestamp(\"2026-01-01T00:01:00Z\")")]
    [InlineData("anyOf('ts') - anyOf('dur')", "timestamp(\"2025-12-31T23:59:00Z\")")]
    [InlineData("anyOf('dur') + anyOf('dur')", "duration(\"120s\")")]
    [InlineData("anyOf('dur') - anyOf('dur')", "duration(\"0s\")")]
    [InlineData("abs(anyOf('int'))", "5")]
    [InlineData("int(anyOf('dec'))", "2")]
    [InlineData("string(anyOf('dec'))", "\"2.5\"")]
    [InlineData("anyOf('null') == null", "true")]
    public void Dynamic_values_dispatch_at_run_time(string source, string expected) => Run(source).GetValueOrThrow().ToString().ShouldBe(expected);

    [Theory]
    [InlineData("anyOf('str') + 1", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('str') - 1", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('str') * 1", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('str') / 1", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('dec') % 1", RuleErrorCode.InvalidValue)]
    [InlineData("-anyOf('str')", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('null') + 1", RuleErrorCode.NullValue)]
    [InlineData("-anyOf('null')", RuleErrorCode.NullValue)]
    [InlineData("anyOf('str') < 1", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('int') && true", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('null') || false", RuleErrorCode.NullValue)]
    [InlineData("1 in anyOf('int')", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('int')[0]", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('list')['a']", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('int').all(i, true)", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('null').all(i, true)", RuleErrorCode.NullValue)]
    [InlineData("size(anyOf('int'))", RuleErrorCode.InvalidValue)]
    [InlineData("abs(anyOf('str'))", RuleErrorCode.InvalidValue)]
    [InlineData("int(anyOf('bool'))", RuleErrorCode.InvalidValue)]
    [InlineData("decimal(anyOf('bool'))", RuleErrorCode.InvalidValue)]
    [InlineData("string(anyOf('list'))", RuleErrorCode.InvalidValue)]
    [InlineData("date(anyOf('int'))", RuleErrorCode.InvalidValue)]
    [InlineData("timestamp(anyOf('int'))", RuleErrorCode.InvalidValue)]
    [InlineData("duration(anyOf('int'))", RuleErrorCode.InvalidValue)]
    [InlineData("ageAt(anyOf('int'), dt)", RuleErrorCode.InvalidValue)]
    [InlineData("addDays(dt, anyOf('str'))", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('int').startsWith('a')", RuleErrorCode.InvalidValue)]
    [InlineData("round(anyOf('str'), 2, 'HalfUp')", RuleErrorCode.InvalidValue)]
    [InlineData("anyOf('mixed').map(i, i + 1)", RuleErrorCode.InvalidValue)]
    public void Dynamic_type_errors_are_typed_evaluation_errors(string source, RuleErrorCode code) => Run(source).Error!.Code.ShouldBe(code);

    [Fact]
    public void Dynamic_results_are_checked_against_the_expected_type()
    {
        Run("anyOf('int')", RuleType.Decimal).Value.ShouldBe(new DecimalValue(5m));
        Run("anyOf('list')", RuleType.ListOf(RuleType.Decimal)).Value!.ToString().ShouldBe("[1, 2]");
        Run("anyOf('map')", RuleType.MapOf(RuleType.String, RuleType.Int)).IsSuccess.ShouldBeTrue();
        Run("anyOf('obj')", RuleType.ObjectOf(T.Vehicle)).IsSuccess.ShouldBeTrue();
        Run("anyOf('null')", RuleType.Int.Nullable()).Value.ShouldBe(RuleValue.Null);
        Run("anyOf('null')", RuleType.Int).Error!.Code.ShouldBe(RuleErrorCode.NullValue);
        Run("anyOf('str')", RuleType.Int).Error!.Code.ShouldBe(RuleErrorCode.InvalidValue);
        Run("anyOf('mixed')", RuleType.ListOf(RuleType.Int)).Error!.Code.ShouldBe(RuleErrorCode.InvalidValue);
        Run("anyOf('obj')", RuleType.ObjectOf(T.Driver)).Error!.Code.ShouldBe(RuleErrorCode.InvalidValue);
        Run("anyOf('map')", RuleType.MapOf(RuleType.String, RuleType.String)).Error!.Code.ShouldBe(RuleErrorCode.InvalidValue);
        Run("[anyOf('int')] + [2.5]").Value!.ToString().ShouldBe("[5, 2.5]");
        Env.TryCompile("anyOf('obj').value").Errors.Single().Code.ShouldBe(RuleErrorCode.UnknownField);
        Env.TryCompile("anyOf('null') + null").Errors.Single().Code.ShouldBe(RuleErrorCode.NoMatchingOverload);
    }

    [Theory]
    [InlineData("owner.name", RuleErrorCode.NullValue)]
    [InlineData("has(owner.name)", RuleErrorCode.NullValue)]
    [InlineData("extras.a", RuleErrorCode.NullValue)]
    [InlineData("has(extras.a)", RuleErrorCode.NullValue)]
    [InlineData("extras.all(k, true)", RuleErrorCode.NullValue)]
    [InlineData("{ns: 1}", RuleErrorCode.NullValue)]
    [InlineData("ns.matches('a')", RuleErrorCode.NullValue)]
    [InlineData("ns.startsWith('a')", RuleErrorCode.NullValue)]
    [InlineData("ns.lowerAscii()", RuleErrorCode.NullValue)]
    [InlineData("size(ns)", RuleErrorCode.NullValue)]
    [InlineData("ageAt(nd, dt)", RuleErrorCode.NullValue)]
    [InlineData("year(nd)", RuleErrorCode.NullValue)]
    [InlineData("addDays(dt, ni)", RuleErrorCode.NullValue)]
    [InlineData("ints[ni]", RuleErrorCode.NullValue)]
    [InlineData("sum(nl)", RuleErrorCode.NullValue)]
    [InlineData("max(nl)", RuleErrorCode.NullValue)]
    [InlineData("min([n])", RuleErrorCode.NullValue)]
    [InlineData("max(n, 1.0)", RuleErrorCode.NullValue)]
    [InlineData("1 in nl", RuleErrorCode.NullValue)]
    [InlineData("int(ns)", RuleErrorCode.NullValue)]
    [InlineData("string(ni)", RuleErrorCode.NullValue)]
    [InlineData("round(1.5, ni, 'HalfUp')", RuleErrorCode.NullValue)]
    public void Null_values_fail_with_null_value_errors(string source, RuleErrorCode code) => T.EvalError(source).Code.ShouldBe(code);

    [Fact]
    public void Null_aware_expressions_succeed()
    {
        T.Eval("owner == null ? 'none' : owner.name").ShouldBe("\"none\"");
        T.Eval("has(vehicle.trackerFitted) || ns == null").ShouldBe("true");
        T.Eval("null in [1, 2]").ShouldBe("false");
    }

    [Fact]
    public void Extreme_durations_overflow_as_typed_errors()
    {
        var inputs = T.Inputs().Set("dur", TimeSpan.MinValue).Build();
        string Code(string source) => T.Env.Compile(source).Evaluate(inputs).Error!.CodeText;
        Code("ts - dur").ShouldBe("RULE-OVERFLOW");
        Code("-dur").ShouldBe("RULE-OVERFLOW");
        Code("duration('1s') - dur").ShouldBe("RULE-OVERFLOW");
        Code("dur + dur").ShouldBe("RULE-OVERFLOW");
        Code("dur + ts").ShouldBe("RULE-OVERFLOW");
    }

    [Theory]
    [InlineData("duration('')")]
    [InlineData("duration('-')")]
    [InlineData("duration('1')")]
    [InlineData("duration('1x')")]
    [InlineData("duration('s')")]
    [InlineData("duration('1.5.5s')")]
    [InlineData("duration('0.5ns')")]
    [InlineData("duration('99999999999999999999999h')")]
    [InlineData("duration('9999999999999999999999999999h')")]
    [InlineData("timestamp('2026-11-01T10:00:00')")]
    [InlineData("timestamp('2026-11-01T10:00:00+0200')")]
    [InlineData("timestamp('2026-13-01T10:00:00Z')")]
    [InlineData("date('01/11/2026')")]
    public void Invalid_time_literals_fail_compilation(string source) => T.CompileError(source).Code.ShouldBe(RuleErrorCode.InvalidLiteral);

    [Theory]
    [InlineData("\"\\u12\"")]
    [InlineData("\"\\U0000004")]
    public void Truncated_escapes_are_syntax_errors(string source) => T.CompileError(source).Code.ShouldBe(RuleErrorCode.Syntax);

    [Fact]
    public void Misc_binder_paths()
    {
        T.CompileError("drivers[0].foo").Message.ShouldContain("(expression).foo");
        T.CompileError("mkt.sub.fn(1)").Message.ShouldContain("mkt.sub.fn");
        T.Eval("[{'a': 1}, {'b': 2.5}]").ShouldBe("[{\"a\": 1}, {\"b\": 2.5}]");
        T.CompileError("[{'a': 1}, {1: 2}]").Code.ShouldBe(RuleErrorCode.TypeMismatch);
        T.Eval("{'a': 1} == {'a': 1, 'b': 2}").ShouldBe("false");
        T.Eval("{1: 'a'} == {1: 'a'}").ShouldBe("true");
        T.Eval("vehicle == vehicle").ShouldBe("true");
        T.Eval("decimal('.5')").ShouldBe("0.5");
        T.EvalError("decimal('1.')").Code.ShouldBe(RuleErrorCode.InvalidValue);
        T.EvalError("decimal('')").Code.ShouldBe(RuleErrorCode.InvalidValue);
        T.EvalError("decimal('1.2.3')").Code.ShouldBe(RuleErrorCode.InvalidValue);
        T.EvalError("decimal('-')").Code.ShouldBe(RuleErrorCode.InvalidValue);
        RuleValue.List(1).Kind.ShouldBe(RuleTypeKind.List);
        T.Default["names"].Kind.ShouldBe(RuleTypeKind.Map);
        T.Default["vehicle"].Kind.ShouldBe(RuleTypeKind.Object);
        var widened = T.Env.Compile("ints", RuleType.ListOf(RuleType.Decimal)).Evaluate(T.Default, new EvaluationOptions { Trace = true });
        widened.Trace.Single().Expression.ShouldBe("ints");
        var literal = T.Env.Compile("[1, 2.5]").Evaluate(T.Default, new EvaluationOptions { Trace = true });
        literal.Trace.Single().Value.ToString().ShouldBe("[1, 2.5]");
    }

    [Fact]
    public void Regex_match_timeout_is_a_typed_error()
    {
        var schema = InputSchema.Define().Variable("t", RuleType.String).Build();
        var env = RuleEnvironment.Create(schema, new RuleLimits { RegexTimeout = TimeSpan.FromTicks(1), MaxEvaluationSteps = long.MaxValue / 2 });
        string big = string.Concat(Enumerable.Repeat("ab", 2_000_000));
        var result = env.Compile("t.matches('(a|b)*c$')").Evaluate(schema.NewInputs().Set("t", big).Build());
        result.Error!.Code.ShouldBe(RuleErrorCode.RegexTimeout);
        env.Compile("t.matches('(a|b)*c$') || true").Evaluate(schema.NewInputs().Set("t", big).Build()).Error!.Code.ShouldBe(RuleErrorCode.RegexTimeout);
    }
}
