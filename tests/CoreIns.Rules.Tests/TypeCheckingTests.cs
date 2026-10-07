using System;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

public class TypeCheckingTests
{
    [Fact]
    public void Unknown_input_fails_with_its_name_and_position()
    {
        var error = T.CompileError("x + engineSize");
        error.Code.ShouldBe(RuleErrorCode.UnknownIdentifier);
        error.Message.ShouldContain("'engineSize'");
        error.Position.ShouldBe(new SourcePosition(4, 1, 5));
    }

    [Fact]
    public void Unknown_field_fails_with_the_field_path()
    {
        var error = T.CompileError("policy.effectiveDate < date(\"2027-01-01\") && vehicle.colour == \"red\"");
        error.Code.ShouldBe(RuleErrorCode.UnknownField);
        error.Message.ShouldContain("vehicle.colour");
        error.Position!.Value.Column.ShouldBe(46);
    }

    [Theory]
    [InlineData("foo(1)", RuleErrorCode.UnknownFunction)]
    [InlineData("s.reverse()", RuleErrorCode.UnknownFunction)]
    [InlineData("x.abs()", RuleErrorCode.UnknownFunction)]
    [InlineData("startsWith(s, \"M\")", RuleErrorCode.UnknownFunction)]
    [InlineData("math.greatest(1, 2)", RuleErrorCode.UnknownFunction)]
    [InlineData("s.split(\",\")", RuleErrorCode.UnknownFunction)]
    [InlineData("now()", RuleErrorCode.NonDeterministic)]
    [InlineData("today()", RuleErrorCode.NonDeterministic)]
    [InlineData("random()", RuleErrorCode.NonDeterministic)]
    [InlineData("now", RuleErrorCode.NonDeterministic)]
    [InlineData("dt < today", RuleErrorCode.NonDeterministic)]
    [InlineData("clock.now()", RuleErrorCode.NonDeterministic)]
    [InlineData("double(1)", RuleErrorCode.UnsupportedFeature)]
    [InlineData("uint(1)", RuleErrorCode.UnsupportedFeature)]
    [InlineData("dyn(1)", RuleErrorCode.UnsupportedFeature)]
    [InlineData("type(1)", RuleErrorCode.UnsupportedFeature)]
    [InlineData("ts.getFullYear()", RuleErrorCode.UnsupportedFeature)]
    [InlineData("1 + \"a\"", RuleErrorCode.NoMatchingOverload)]
    [InlineData("s - s", RuleErrorCode.NoMatchingOverload)]
    [InlineData("x % 1.5", RuleErrorCode.NoMatchingOverload)]
    [InlineData("dt + dur", RuleErrorCode.NoMatchingOverload)]
    [InlineData("ts * 2", RuleErrorCode.NoMatchingOverload)]
    [InlineData("null + 1", RuleErrorCode.NoMatchingOverload)]
    [InlineData("[] + null", RuleErrorCode.NoMatchingOverload)]
    [InlineData("-s", RuleErrorCode.NoMatchingOverload)]
    [InlineData("s < 1", RuleErrorCode.NoMatchingOverload)]
    [InlineData("ints < ints", RuleErrorCode.NoMatchingOverload)]
    [InlineData("1 == \"1\"", RuleErrorCode.NoMatchingOverload)]
    [InlineData("vehicle == policy", RuleErrorCode.NoMatchingOverload)]
    [InlineData("x in tags", RuleErrorCode.NoMatchingOverload)]
    [InlineData("1 in names", RuleErrorCode.NoMatchingOverload)]
    [InlineData("\"a\" in s", RuleErrorCode.NoMatchingOverload)]
    [InlineData("ints[\"a\"]", RuleErrorCode.NoMatchingOverload)]
    [InlineData("names[1]", RuleErrorCode.NoMatchingOverload)]
    [InlineData("x[0]", RuleErrorCode.NoMatchingOverload)]
    [InlineData("x && true", RuleErrorCode.TypeMismatch)]
    [InlineData("b || 1", RuleErrorCode.TypeMismatch)]
    [InlineData("!x", RuleErrorCode.TypeMismatch)]
    [InlineData("x ? 1 : 2", RuleErrorCode.TypeMismatch)]
    [InlineData("b ? 1 : \"a\"", RuleErrorCode.TypeMismatch)]
    [InlineData("[1, \"a\"]", RuleErrorCode.TypeMismatch)]
    [InlineData("{1: \"a\", \"b\": \"c\"}", RuleErrorCode.TypeMismatch)]
    [InlineData("{\"a\": 1, \"b\": \"c\"}", RuleErrorCode.TypeMismatch)]
    [InlineData("{1.5: 1}", RuleErrorCode.TypeMismatch)]
    [InlineData("{\"a\": 1, \"a\": 2}", RuleErrorCode.DuplicateKey)]
    [InlineData("ints.all(i, i)", RuleErrorCode.TypeMismatch)]
    [InlineData("ints.filter(i, 1)", RuleErrorCode.TypeMismatch)]
    [InlineData("ints.map(i, i, i)", RuleErrorCode.TypeMismatch)]
    [InlineData("x.all(i, true)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("ints.all(i, j > 0)", RuleErrorCode.UnknownIdentifier)]
    [InlineData("x.y", RuleErrorCode.UnknownField)]
    [InlineData("ints.first", RuleErrorCode.UnknownField)]
    [InlineData("has(x.y)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("has(vehicle.colour)", RuleErrorCode.UnknownField)]
    [InlineData("size()", RuleErrorCode.NoMatchingOverload)]
    [InlineData("size(x)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("s.size(1)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("x.startsWith(\"a\")", RuleErrorCode.NoMatchingOverload)]
    [InlineData("s.startsWith(1)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("int(b)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("decimal(dt)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("string(ints)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("date(1)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("round(d, 2)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("round(s, 2, \"HalfUp\")", RuleErrorCode.NoMatchingOverload)]
    [InlineData("round(d, 2, s)", RuleErrorCode.InvalidArgument)]
    [InlineData("round(d, 2, \"HALF_UP\")", RuleErrorCode.InvalidArgument)]
    [InlineData("round(d, 29, \"HalfUp\")", RuleErrorCode.InvalidArgument)]
    [InlineData("round(d, -1, \"HalfUp\")", RuleErrorCode.InvalidArgument)]
    [InlineData("abs(s)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("min(1)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("min()", RuleErrorCode.NoMatchingOverload)]
    [InlineData("max(1, \"a\")", RuleErrorCode.NoMatchingOverload)]
    [InlineData("max(ints, ints)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("min([vehicle])", RuleErrorCode.NoMatchingOverload)]
    [InlineData("sum(tags)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("sum(x)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("ageAt(s, dt)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("addDays(dt, 1.5)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("year(ts)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("s.matches(s)", RuleErrorCode.InvalidArgument)]
    [InlineData("s.matches(\"(\")", RuleErrorCode.InvalidRegex)]
    [InlineData("s.matches(\"(a)\\\\1\")", RuleErrorCode.InvalidRegex)]
    [InlineData("x.matches(\"a\")", RuleErrorCode.NoMatchingOverload)]
    [InlineData("matches(s)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("date(\"2026-13-01\")", RuleErrorCode.InvalidLiteral)]
    [InlineData("timestamp(\"2026-11-01T10:00:00\")", RuleErrorCode.InvalidLiteral)]
    [InlineData("duration(\"1d\")", RuleErrorCode.InvalidLiteral)]
    [InlineData("mkt.round(d)", RuleErrorCode.NoMatchingOverload)]
    [InlineData("mkt.round(s, \"x\")", RuleErrorCode.NoMatchingOverload)]
    [InlineData("mkt.unknown(d)", RuleErrorCode.UnknownFunction)]
    public void Rejects_ill_typed_or_unknown(string source, RuleErrorCode code) => T.CompileError(source).Code.ShouldBe(code);

    [Fact]
    public void Regex_pattern_length_is_limited()
    {
        var env = RuleEnvironment.Create(T.Schema, new RuleLimits { MaxRegexPatternLength = 3 });
        T.CompileError("s.matches(\"abcd\")", env: env).Code.ShouldBe(RuleErrorCode.InvalidRegex);
    }

    [Fact]
    public void Expected_result_type_is_enforced_and_int_widens_to_decimal()
    {
        T.CompileError("s", RuleType.Decimal).Code.ShouldBe(RuleErrorCode.ResultTypeMismatch);
        T.CompileError("ints", RuleType.ListOf(RuleType.String)).Code.ShouldBe(RuleErrorCode.ResultTypeMismatch);

        var widened = T.Env.Compile("x", RuleType.Decimal);
        widened.ResultType.ShouldBe(RuleType.Decimal);
        widened.Evaluate(T.Default).Value.ShouldBeOfType<DecimalValue>().Value.ShouldBe(7m);

        var list = T.Env.Compile("ints", RuleType.ListOf(RuleType.Decimal));
        list.Evaluate(T.Default).Value.ShouldBeOfType<ListValue>().Items.ShouldAllBe(v => v is DecimalValue);

        var map = T.Env.Compile("names", RuleType.MapOf(RuleType.String, RuleType.Decimal));
        map.Evaluate(T.Default).Value.ShouldBeOfType<MapValue>().Entries.ShouldAllBe(e => e.Value is DecimalValue);

        T.Env.Compile("null", RuleType.Decimal.Nullable()).Evaluate(T.Default).Value.ShouldBe(RuleValue.Null);
        T.Env.Compile("[]", RuleType.ListOf(RuleType.Int)).Evaluate(T.Default).Value!.ToString().ShouldBe("[]");
        T.Env.Compile("x", RuleType.Dyn).ResultType.ShouldBe(RuleType.Dyn);
    }

    [Theory]
    [InlineData("x + y", "int")]
    [InlineData("x + d", "decimal")]
    [InlineData("x / y", "int")]
    [InlineData("d / y", "decimal")]
    [InlineData("b ? x : d", "decimal")]
    [InlineData("[1, 2.5]", "list(decimal)")]
    [InlineData("[[1], [2.5]]", "list(list(decimal))")]
    [InlineData("[]", "list(dyn)")]
    [InlineData("[null, 1]", "list(int)")]
    [InlineData("{\"a\": 1}", "map(string, int)")]
    [InlineData("{\"a\": 1, \"b\": 2.0}", "map(string, decimal)")]
    [InlineData("{}", "map(dyn, dyn)")]
    [InlineData("ints.map(i, i * 1.5)", "list(decimal)")]
    [InlineData("drivers.filter(dr, dr.isMain)", "list(Driver)")]
    [InlineData("names.map(k, k)", "list(string)")]
    [InlineData("ts - ts", "duration")]
    [InlineData("ts + dur", "timestamp")]
    [InlineData("dur + ts", "timestamp")]
    [InlineData("sum(decs)", "decimal")]
    [InlineData("max(x, d)", "decimal")]
    [InlineData("min(tags)", "string")]
    [InlineData("vehicle", "Vehicle")]
    [InlineData("null", "null_type")]
    [InlineData("names.a", "int")]
    [InlineData("mkt.round(x, \"charge.line\")", "decimal")]
    [InlineData("[].map(i, i)", "list(dyn)")]
    public void Infers_static_types(string source, string type) => T.Env.Compile(source).ResultType.ToString().ShouldBe(type);

    [Fact]
    public void Rule_types_compare_structurally()
    {
        RuleType.ListOf(RuleType.Int).ShouldBe(RuleType.ListOf(RuleType.Int));
        RuleType.ListOf(RuleType.Int).GetHashCode().ShouldBe(RuleType.ListOf(RuleType.Int).GetHashCode());
        RuleType.ListOf(RuleType.Int).ShouldNotBe(RuleType.ListOf(RuleType.Decimal));
        RuleType.MapOf(RuleType.String, RuleType.Int).Equals((object)RuleType.MapOf(RuleType.String, RuleType.Int)).ShouldBeTrue();
        RuleType.ObjectOf(T.Driver).ShouldNotBe(RuleType.ObjectOf(T.Vehicle));
        RuleType.Int.Equals(null).ShouldBeFalse();
        RuleType.Int.IsNumeric.ShouldBeTrue();
        RuleType.String.IsNumeric.ShouldBeFalse();
        Should.Throw<ArgumentException>(() => RuleType.MapOf(RuleType.Decimal, RuleType.Int));
        new[] { RuleType.Bool, RuleType.Date, RuleType.Timestamp, RuleType.Duration, RuleType.Dyn }
            .ShouldAllBe(t => !string.IsNullOrEmpty(t.ToString()));
    }
}
