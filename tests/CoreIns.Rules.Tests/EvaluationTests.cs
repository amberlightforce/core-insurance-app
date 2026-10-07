using System;
using System.Linq;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

public class EvaluationTests
{
    [Theory]
    [InlineData("x + y", "9")]
    [InlineData("x - y", "5")]
    [InlineData("x * y", "14")]
    [InlineData("x / y", "3")]
    [InlineData("-x / y", "-3")]
    [InlineData("x % y", "1")]
    [InlineData("-x % y", "-1")]
    [InlineData("x % -1", "0")]
    [InlineData("d + e", "13.50")]
    [InlineData("d - e", "7.50")]
    [InlineData("d * e", "31.50")]
    [InlineData("d / 4", "2.625")]
    [InlineData("x + d", "17.50")]
    [InlineData("d - x", "3.50")]
    [InlineData("1 / 3.0", "0.3333333333333333333333333333")]
    [InlineData("0.1 + 0.2 == 0.3", "true")]
    [InlineData("premium * 0.15", "15.0150")]
    [InlineData("-d", "-10.50")]
    [InlineData("-(-d)", "10.50")]
    public void Arithmetic(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("s + \"!\"", "\"Motor Policy!\"")]
    [InlineData("size(s)", "12")]
    [InlineData("s.size()", "12")]
    [InlineData("\"ΑΒΓ\".size()", "3")]
    [InlineData("\"😀\".size()", "1")]
    [InlineData("s.startsWith(\"Motor\")", "true")]
    [InlineData("s.startsWith(\"motor\")", "false")]
    [InlineData("s.endsWith(\"Policy\")", "true")]
    [InlineData("s.contains(\"or P\")", "true")]
    [InlineData("\"Hello\".lowerAscii()", "\"hello\"")]
    [InlineData("\"Ωmega Ä\".upperAscii()", "\"ΩMEGA Ä\"")]
    [InlineData("\"  a b \".trim()", "\"a b\"")]
    [InlineData("s.matches(\"^Motor\")", "true")]
    [InlineData("matches(s, \"policy$\")", "false")]
    [InlineData("\"ABC123\".matches(\"^[A-Z]{3}[0-9]{3}$\")", "true")]
    [InlineData("\"ΙΚΑ-1234\".matches(\"^[Α-Ω]{3}-\\\\d{4}$\")", "true")]
    public void Strings(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("x > y", "true")]
    [InlineData("x <= y", "false")]
    [InlineData("x >= 7", "true")]
    [InlineData("x < 7.5", "true")]
    [InlineData("d >= 10.5", "true")]
    [InlineData("x == 7.0", "true")]
    [InlineData("1 != 1.0", "false")]
    [InlineData("\"a\" < \"b\"", "true")]
    [InlineData("\"b\" <= \"a\"", "false")]
    [InlineData("false < true", "true")]
    [InlineData("dt < date(\"2027-01-01\")", "true")]
    [InlineData("dt == policy.effectiveDate", "true")]
    [InlineData("ts > timestamp(\"2026-01-01T00:00:00Z\")", "true")]
    [InlineData("dur < duration(\"2h\")", "true")]
    [InlineData("dur >= duration(\"60m\")", "true")]
    [InlineData("[1, 2] == [1, 2]", "true")]
    [InlineData("[1, 2] == [1, 2.0]", "true")]
    [InlineData("[1, 2] != [2, 1]", "true")]
    [InlineData("[1] == [1, 2]", "false")]
    [InlineData("{\"a\": 1} == {\"a\": 1}", "true")]
    [InlineData("{\"a\": 1, \"b\": 2} == {\"b\": 2, \"a\": 1}", "true")]
    [InlineData("{\"a\": 1} == {\"a\": 2}", "false")]
    [InlineData("{\"a\": 1} == {\"b\": 1}", "false")]
    [InlineData("null == null", "true")]
    [InlineData("n == null", "true")]
    [InlineData("d != null", "true")]
    [InlineData("vehicle.trackerFitted == null", "true")]
    [InlineData("drivers[0] == drivers[0]", "true")]
    [InlineData("drivers[0] == drivers[1]", "false")]
    public void Comparison(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("true && false", "false")]
    [InlineData("true && true", "true")]
    [InlineData("false || true", "true")]
    [InlineData("false || false", "false")]
    [InlineData("!b", "false")]
    [InlineData("b ? \"y\" : \"n\"", "\"y\"")]
    [InlineData("!b ? 1 : 2.5", "2.5")]
    [InlineData("b ? 1 : 2.5", "1")]
    [InlineData("false && 1 / 0 == 1", "false")]
    [InlineData("true || 1 / 0 == 1", "true")]
    [InlineData("1 / 0 == 1 || true", "true")]
    [InlineData("1 / 0 == 1 && false", "false")]
    [InlineData("b ? x : 1 / 0", "7")]
    [InlineData("!b ? 1 / 0 : x", "7")]
    public void Logic_short_circuits_and_absorbs_errors_like_CEL(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("1 / 0 == 1 && true")]
    [InlineData("true && 1 / 0 == 1")]
    [InlineData("1 / 0 == 1 || false")]
    [InlineData("false || 1 / 0 == 1")]
    [InlineData("1 / 0 == 1 || 2 / 0 == 1")]
    [InlineData("1 / 0 == 1 && 2 / 0 == 1")]
    public void Logic_propagates_errors_when_not_decided(string source) => T.EvalError(source).Code.ShouldBe(RuleErrorCode.DivisionByZero);

    [Theory]
    [InlineData("2 in ints", "true")]
    [InlineData("2.0 in ints", "true")]
    [InlineData("9 in ints", "false")]
    [InlineData("\"a\" in names", "true")]
    [InlineData("\"z\" in names", "false")]
    [InlineData("\"urban\" in tags", "true")]
    [InlineData("vehicle.usage in [\"PRIVATE\", \"TAXI\"]", "true")]
    [InlineData("ints[0]", "1")]
    [InlineData("ints[2]", "3")]
    [InlineData("names[\"b\"]", "2")]
    [InlineData("names.a", "1")]
    [InlineData("[[1, 2], [3]][1][0]", "3")]
    [InlineData("{1: \"one\", 2: \"two\"}[2]", "\"two\"")]
    [InlineData("{true: 1}[true]", "1")]
    [InlineData("ints + [4]", "[1, 2, 3, 4]")]
    [InlineData("[] + ints", "[1, 2, 3]")]
    [InlineData("[1, 2.5]", "[1, 2.5]")]
    [InlineData("size(ints)", "3")]
    [InlineData("ints.size()", "3")]
    [InlineData("size([])", "0")]
    [InlineData("size(names)", "2")]
    [InlineData("{\"a\": 1}.size()", "1")]
    [InlineData("{\"k\": [1, 2]}[\"k\"][1]", "2")]
    [InlineData("drivers[0].name", "\"Maria\"")]
    [InlineData("drivers[1].claimsLast3Years", "1")]
    public void Collections(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("ints.all(i, i > 0)", "true")]
    [InlineData("ints.all(i, i > 1)", "false")]
    [InlineData("ints.exists(i, i > 2)", "true")]
    [InlineData("ints.exists(i, i > 3)", "false")]
    [InlineData("ints.exists_one(i, i > 1)", "false")]
    [InlineData("ints.exists_one(i, i > 2)", "true")]
    [InlineData("ints.map(i, i * 2)", "[2, 4, 6]")]
    [InlineData("ints.map(i, i > 1, i * 10)", "[20, 30]")]
    [InlineData("ints.filter(i, i % 2 == 1)", "[1, 3]")]
    [InlineData("decs.map(v, round(v * 1.1, 2, \"HalfUp\"))", "[1.65, 2.48]")]
    [InlineData("names.all(k, k.size() == 1)", "true")]
    [InlineData("names.map(k, names[k])", "[1, 2]")]
    [InlineData("names.filter(k, names[k] > 1)", "[\"b\"]")]
    [InlineData("drivers.exists(dr, ageAt(dr.birthDate, policy.effectiveDate) < 25)", "true")]
    [InlineData("drivers.filter(dr, dr.isMain).map(dr, dr.name)", "[\"Maria\"]")]
    [InlineData("sum(drivers.map(dr, dr.claimsLast3Years))", "1")]
    [InlineData("[[1, 2], [3]].all(l, l.all(i, i > 0))", "true")]
    [InlineData("[[1, 2], [3]].map(l, l.map(i, i * x))", "[[7, 14], [21]]")]
    [InlineData("ints.map(x, x + 1)", "[2, 3, 4]")]
    [InlineData("ints.map(x, x + 1).map(i, i + x)", "[9, 10, 11]")]
    [InlineData("[].all(i, i > 0)", "true")]
    [InlineData("[].exists(i, i > 0)", "false")]
    [InlineData("[].map(i, i)", "[]")]
    [InlineData("[0, 1].exists(i, 1 / i > 0)", "true")]
    [InlineData("[0, -1].all(i, 1 / i > 0)", "false")]
    [InlineData("has(vehicle.trackerFitted)", "false")]
    [InlineData("has(vehicle.value)", "true")]
    [InlineData("has(names.a)", "true")]
    [InlineData("has(names.z)", "false")]
    [InlineData("has(vehicle.trackerFitted) ? vehicle.trackerFitted : false", "false")]
    public void Macros(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("int(d)", "10")]
    [InlineData("int(-2.7)", "-2")]
    [InlineData("int(x)", "7")]
    [InlineData("int(\"42\")", "42")]
    [InlineData("int(\"-42\")", "-42")]
    [InlineData("decimal(x)", "7")]
    [InlineData("decimal(d)", "10.50")]
    [InlineData("decimal(\"1.25\")", "1.25")]
    [InlineData("decimal(\"-0.5\")", "-0.5")]
    [InlineData("string(d)", "\"10.50\"")]
    [InlineData("string(x)", "\"7\"")]
    [InlineData("string(s)", "\"Motor Policy\"")]
    [InlineData("string(true)", "\"true\"")]
    [InlineData("string(dt)", "\"2026-11-01\"")]
    [InlineData("string(ts)", "\"2026-11-01T10:00:00Z\"")]
    [InlineData("string(timestamp(\"2026-11-01T10:00:00.25Z\"))", "\"2026-11-01T10:00:00.25Z\"")]
    [InlineData("string(dur)", "\"3600s\"")]
    [InlineData("string(duration(\"1.5s\"))", "\"1.5s\"")]
    [InlineData("string(-duration(\"1.5s\"))", "\"-1.5s\"")]
    [InlineData("date(\"2026-11-01\")", "date(\"2026-11-01\")")]
    [InlineData("date(ts)", "date(\"2026-11-01\")")]
    [InlineData("date(dt)", "date(\"2026-11-01\")")]
    [InlineData("date(s.size() > 0 ? \"2026-01-02\" : \"\")", "date(\"2026-01-02\")")]
    [InlineData("timestamp(ts)", "timestamp(\"2026-11-01T10:00:00Z\")")]
    [InlineData("timestamp(\"2026-11-01T12:00:00+02:00\") == ts", "true")]
    [InlineData("timestamp(b ? \"2026-11-01T10:00:00Z\" : \"\") == ts", "true")]
    [InlineData("duration(dur)", "duration(\"3600s\")")]
    [InlineData("duration(b ? \"30m\" : \"\")", "duration(\"1800s\")")]
    [InlineData("duration(\"1h30m\") == duration(\"5400s\")", "true")]
    [InlineData("duration(\"-90s\")", "duration(\"-90s\")")]
    [InlineData("duration(\"+1ms\")", "duration(\"0.001s\")")]
    [InlineData("duration(\"1us\") + duration(\"100ns\")", "duration(\"0.0000011s\")")]
    public void Conversions(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("ageAt(date(\"2009-01-15\"), date(\"2026-11-01\"))", "17")]
    [InlineData("ageAt(date(\"2009-11-01\"), date(\"2026-11-01\"))", "17")]
    [InlineData("ageAt(date(\"2009-11-02\"), date(\"2026-11-01\"))", "16")]
    [InlineData("ageAt(date(\"2008-02-29\"), date(\"2026-02-28\"))", "18")]
    [InlineData("ageAt(date(\"2008-02-29\"), date(\"2026-02-27\"))", "17")]
    [InlineData("ageAt(dt, dt)", "0")]
    [InlineData("yearsBetween(date(\"2026-11-01\"), date(\"2009-01-15\"))", "-17")]
    [InlineData("monthsBetween(date(\"2026-01-31\"), date(\"2026-02-28\"))", "1")]
    [InlineData("monthsBetween(date(\"2026-01-15\"), date(\"2026-03-14\"))", "1")]
    [InlineData("monthsBetween(date(\"2026-03-14\"), date(\"2026-01-15\"))", "-1")]
    [InlineData("daysBetween(date(\"2026-01-01\"), date(\"2027-01-01\"))", "365")]
    [InlineData("daysBetween(date(\"2028-03-01\"), date(\"2028-02-01\"))", "-29")]
    [InlineData("addDays(dt, 30)", "date(\"2026-12-01\")")]
    [InlineData("addDays(dt, -1)", "date(\"2026-10-31\")")]
    [InlineData("addMonths(date(\"2026-01-31\"), 1)", "date(\"2026-02-28\")")]
    [InlineData("addMonths(dt, 12) == addYears(dt, 1)", "true")]
    [InlineData("addYears(date(\"2024-02-29\"), 1)", "date(\"2025-02-28\")")]
    [InlineData("year(dt)", "2026")]
    [InlineData("month(dt)", "11")]
    [InlineData("day(dt)", "1")]
    [InlineData("dayOfWeek(dt)", "7")]
    [InlineData("dayOfWeek(date(\"2026-11-02\"))", "1")]
    [InlineData("ts + duration(\"90m\")", "timestamp(\"2026-11-01T11:30:00Z\")")]
    [InlineData("duration(\"90m\") + ts", "timestamp(\"2026-11-01T11:30:00Z\")")]
    [InlineData("ts - duration(\"10h\")", "timestamp(\"2026-11-01T00:00:00Z\")")]
    [InlineData("ts - timestamp(\"2026-11-01T00:00:00Z\")", "duration(\"36000s\")")]
    [InlineData("dur + dur", "duration(\"7200s\")")]
    [InlineData("dur - duration(\"30m\")", "duration(\"1800s\")")]
    [InlineData("-dur", "duration(\"-3600s\")")]
    public void Dates_and_times(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("2.345", 2, "HalfEven", "2.34")]
    [InlineData("2.345", 2, "HalfUp", "2.35")]
    [InlineData("2.345", 2, "HalfDown", "2.34")]
    [InlineData("2.345", 2, "Up", "2.35")]
    [InlineData("2.345", 2, "Down", "2.34")]
    [InlineData("2.345", 2, "Ceiling", "2.35")]
    [InlineData("2.345", 2, "Floor", "2.34")]
    [InlineData("-2.345", 2, "HalfEven", "-2.34")]
    [InlineData("-2.345", 2, "HalfUp", "-2.35")]
    [InlineData("-2.345", 2, "HalfDown", "-2.34")]
    [InlineData("-2.345", 2, "Up", "-2.35")]
    [InlineData("-2.345", 2, "Down", "-2.34")]
    [InlineData("-2.345", 2, "Ceiling", "-2.34")]
    [InlineData("-2.345", 2, "Floor", "-2.35")]
    [InlineData("2.3451", 2, "HalfDown", "2.35")]
    [InlineData("-2.3451", 2, "HalfDown", "-2.35")]
    [InlineData("2.3449", 2, "HalfDown", "2.34")]
    [InlineData("2.355", 2, "HalfEven", "2.36")]
    [InlineData("2.341", 2, "Up", "2.35")]
    [InlineData("2.349", 2, "Down", "2.34")]
    [InlineData("15", 2, "HalfUp", "15.00")]
    [InlineData("15.015", 2, "HalfUp", "15.02")]
    [InlineData("0.5", 0, "HalfEven", "0")]
    [InlineData("1.5", 0, "HalfEven", "2")]
    [InlineData("2.5", 0, "HalfUp", "3")]
    [InlineData("1.23", 5, "HalfUp", "1.23000")]
    public void Explicit_rounding_modes(string value, int places, string mode, string expected) =>
        T.Eval($"round({value}, {places}, \"{mode}\")").ShouldBe(expected);

    [Theory]
    [InlineData("min(3, 1, 2)", "1")]
    [InlineData("max(1, 2.5)", "2.5")]
    [InlineData("max(ints)", "3")]
    [InlineData("min(decs)", "1.5")]
    [InlineData("min(tags)", "\"urban\"")]
    [InlineData("max(dt, date(\"2027-01-01\"))", "date(\"2027-01-01\")")]
    [InlineData("sum(ints)", "6")]
    [InlineData("sum(decs)", "3.75")]
    [InlineData("sum([])", "0")]
    [InlineData("sum([1, 2.5])", "3.5")]
    [InlineData("abs(-3)", "3")]
    [InlineData("abs(-2.5)", "2.5")]
    [InlineData("abs(x)", "7")]
    [InlineData("round(x, 0, \"HalfUp\")", "7")]
    [InlineData("round(d, y, \"HalfUp\")", "10.50")]
    public void Math_functions(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("x / (y - 2)", RuleErrorCode.DivisionByZero)]
    [InlineData("x % (y - 2)", RuleErrorCode.DivisionByZero)]
    [InlineData("d / 0", RuleErrorCode.DivisionByZero)]
    [InlineData("9223372036854775807 + 1", RuleErrorCode.Overflow)]
    [InlineData("-9223372036854775808 - 1", RuleErrorCode.Overflow)]
    [InlineData("9223372036854775807 * 2", RuleErrorCode.Overflow)]
    [InlineData("-9223372036854775808 / -1", RuleErrorCode.Overflow)]
    [InlineData("-(-9223372036854775808)", RuleErrorCode.Overflow)]
    [InlineData("abs(-9223372036854775808)", RuleErrorCode.Overflow)]
    [InlineData("7922816251426433759354395.033 * 100000.0", RuleErrorCode.Overflow)]
    [InlineData("7922816251426433759354395.033 * 10000.0 + 7922816251426433759354395.033 * 10000.0", RuleErrorCode.Overflow)]
    [InlineData("-7922816251426433759354395.033 * 10000.0 - 7922816251426433759354395.033 * 10000.0", RuleErrorCode.Overflow)]
    [InlineData("7922816251426433759354395.033 * 10000.0 / 0.1", RuleErrorCode.Overflow)]
    [InlineData("int(7922816251426433759354395.033 * 10000.0)", RuleErrorCode.Overflow)]
    [InlineData("addYears(dt, 9000)", RuleErrorCode.Overflow)]
    [InlineData("addDays(dt, 9223372036854775807)", RuleErrorCode.Overflow)]
    [InlineData("ts + duration(\"900000000000s\")", RuleErrorCode.Overflow)]
    [InlineData("ts - duration(\"900000000000s\")", RuleErrorCode.Overflow)]
    [InlineData("int(\"x\")", RuleErrorCode.InvalidValue)]
    [InlineData("decimal(\"1e5\")", RuleErrorCode.InvalidValue)]
    [InlineData("date(s)", RuleErrorCode.InvalidValue)]
    [InlineData("timestamp(s)", RuleErrorCode.InvalidValue)]
    [InlineData("duration(s)", RuleErrorCode.InvalidValue)]
    [InlineData("ageAt(addDays(dt, 1), dt)", RuleErrorCode.InvalidValue)]
    [InlineData("min([])", RuleErrorCode.InvalidValue)]
    [InlineData("round(d, x * 10, \"HalfUp\")", RuleErrorCode.InvalidValue)]
    [InlineData("ints[3]", RuleErrorCode.IndexOutOfRange)]
    [InlineData("ints[-1]", RuleErrorCode.IndexOutOfRange)]
    [InlineData("names[\"z\"]", RuleErrorCode.NoSuchKey)]
    [InlineData("names.z", RuleErrorCode.NoSuchKey)]
    [InlineData("{s: 1, \"Motor Policy\": 2}", RuleErrorCode.DuplicateKey)]
    [InlineData("n + 1", RuleErrorCode.NullValue)]
    [InlineData("n > 1", RuleErrorCode.NullValue)]
    [InlineData("-n", RuleErrorCode.NullValue)]
    [InlineData("round(n, 2, \"HalfUp\")", RuleErrorCode.NullValue)]
    [InlineData("vehicle.trackerFitted && true", RuleErrorCode.NullValue)]
    [InlineData("!vehicle.trackerFitted", RuleErrorCode.NullValue)]
    [InlineData("vehicle.trackerFitted ? 1 : 2", RuleErrorCode.NullValue)]
    [InlineData("[0, 1].all(i, 1 / i > 0)", RuleErrorCode.DivisionByZero)]
    [InlineData("[0, 1].exists_one(i, 1 / i > 0)", RuleErrorCode.DivisionByZero)]
    [InlineData("[0].map(i, 1 / i)", RuleErrorCode.DivisionByZero)]
    [InlineData("failing(1)", RuleErrorCode.HostFunctionFailed)]
    [InlineData("wrongType()", RuleErrorCode.HostFunctionFailed)]
    public void Typed_evaluation_errors(string source, RuleErrorCode code) => T.EvalError(source).Code.ShouldBe(code);

    [Fact]
    public void Evaluation_error_carries_position_and_fails_closed()
    {
        var result = T.Run("x + 1 > 0 && d / (x - 7) > 1.0");
        result.IsSuccess.ShouldBeFalse();
        result.Value.ShouldBeNull();
        result.Error!.Code.ShouldBe(RuleErrorCode.DivisionByZero);
        result.Error.CodeText.ShouldBe("RULE-DIVISION-BY-ZERO");
        result.Error.Position.ShouldBe(new SourcePosition(13, 1, 14));
        result.Error.ToString().ShouldStartWith("RULE-DIVISION-BY-ZERO at 1:14");
        var ex = Should.Throw<RuleEvaluationException>(() => result.GetValueOrThrow());
        ex.Error.ShouldBe(result.Error);
        Should.Throw<RuleEvaluationException>(() => T.Env.Compile("1 / 0").EvaluateOrThrow(T.Default));
    }

    [Fact]
    public void Host_functions_are_typed_and_pure()
    {
        T.Eval("mkt.round(premium * 0.15, \"charge.line\")").ShouldBe("15.02");
        T.Eval("mkt.round(0.125 + 0.125, \"tax.document\")").ShouldBe("0.25");
        T.Eval("mkt.round(x, \"charge.line\")").ShouldBe("7.00");
        T.Eval("twice(x)").ShouldBe("14");
        T.EvalError("failing(1)").Message.ShouldContain("boom");
    }

    [Fact]
    public void Host_function_registration_is_validated()
    {
        Should.Throw<ArgumentException>(() => new HostFunction("size", Array.Empty<RuleType>(), RuleType.Int, _ => 1));
        Should.Throw<ArgumentException>(() => new HostFunction("now", Array.Empty<RuleType>(), RuleType.Int, _ => 1));
        Should.Throw<ArgumentException>(() => new HostFunction("double", Array.Empty<RuleType>(), RuleType.Int, _ => 1));
        Should.Throw<ArgumentException>(() => new HostFunction("bad name", Array.Empty<RuleType>(), RuleType.Int, _ => 1));
        Should.Throw<ArgumentException>(() => new HostFunction("mkt..round", Array.Empty<RuleType>(), RuleType.Int, _ => 1));
        var f = new HostFunction("f", Array.Empty<RuleType>(), RuleType.Int, _ => 1);
        Should.Throw<ArgumentException>(() => RuleEnvironment.Create(T.Schema, functions: new[] { f, f }));
        RuleEnvironment.Create(T.Schema, functions: new[] { f }).Functions.ShouldHaveSingleItem().Name.ShouldBe("f");
    }

    [Fact]
    public void Qualified_host_name_does_not_shadow_a_declared_input()
    {
        var schema = InputSchema.Define().Variable("mkt", RuleType.MapOf(RuleType.String, RuleType.Int)).Build();
        var env = RuleEnvironment.Create(schema, functions: new[] { T.MktRound });
        env.TryCompile("mkt.round(1.0, \"x\")").Errors.Single().Code.ShouldBe(RuleErrorCode.UnknownFunction);
    }
}
