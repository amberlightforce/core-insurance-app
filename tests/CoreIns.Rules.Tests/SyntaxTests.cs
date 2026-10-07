using System;
using System.Linq;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

public class SyntaxTests
{
    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("10 - 4 - 3", "3")]
    [InlineData("100 / 10 / 5", "2")]
    [InlineData("true || false && false", "true")]
    [InlineData("!false && true", "true")]
    [InlineData("!!true", "true")]
    [InlineData("1 < 2 == true", "true")]
    [InlineData("-x", "-7")]
    [InlineData("--x", "7")]
    [InlineData("x - -1", "8")]
    [InlineData("2 -1", "1")]
    [InlineData("-2 * 3", "-6")]
    [InlineData("true ? 1 : 2", "1")]
    [InlineData("false ? 1 : true ? 2 : 3", "2")]
    [InlineData("1 + // comment\n 2", "3")]
    [InlineData("0x1F", "31")]
    [InlineData("-0x10", "-16")]
    [InlineData("-9223372036854775808", "-9223372036854775808")]
    [InlineData("9223372036854775807", "9223372036854775807")]
    [InlineData(".5", "0.5")]
    [InlineData("1.50", "1.50")]
    [InlineData("-1.25", "-1.25")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("[1, 2, 3,]", "[1, 2, 3]")]
    [InlineData("[]", "[]")]
    [InlineData("{\"a\": 1, \"b\": 2,}[\"b\"]", "2")]
    [InlineData("{}", "{}")]
    [InlineData("\"a\\nb\"", "\"a\\nb\"")]
    [InlineData("'single'", "\"single\"")]
    [InlineData("\"\\u00e9\\x41\\101\\U0001F600\"", "\"éAA😀\"")]
    [InlineData("\"\\a\\b\\f\\r\\t\\v\\\\\\'\\\"\\`\\?\"", "\"\\u0007\\u0008\\u000c\\r\\t\\u000b\\\\'\\\"`?\"")]
    [InlineData("r\"\\n\"", "\"\\\\n\"")]
    [InlineData("R'\\d'", "\"\\\\d\"")]
    [InlineData("\"\"\"multi\n\"line\" ok\"\"\"", "\"multi\\n\\\"line\\\" ok\"")]
    [InlineData("'''it's'''", "\"it's\"")]
    [InlineData("true", "true")]
    [InlineData("null", "null")]
    [InlineData("vehicle.powerKw", "85")]
    [InlineData("(vehicle).usage", "\"PRIVATE\"")]
    public void Parses_and_evaluates(string source, string expected) => T.Eval(source).ShouldBe(expected);

    [Theory]
    [InlineData("9223372036854775808", RuleErrorCode.InvalidLiteral)]
    [InlineData("0x8000000000000000", RuleErrorCode.InvalidLiteral)]
    [InlineData("-0x8000000000000001", RuleErrorCode.InvalidLiteral)]
    [InlineData("0.12345678901234567890123456789", RuleErrorCode.InvalidLiteral)]
    [InlineData("12345678901234567890123456789.0", RuleErrorCode.InvalidLiteral)]
    [InlineData("1e3", RuleErrorCode.UnsupportedFeature)]
    [InlineData("2.5E-3", RuleErrorCode.UnsupportedFeature)]
    [InlineData("1u", RuleErrorCode.UnsupportedFeature)]
    [InlineData("b\"bytes\"", RuleErrorCode.UnsupportedFeature)]
    [InlineData(".x", RuleErrorCode.UnsupportedFeature)]
    [InlineData("12abc", RuleErrorCode.Syntax)]
    [InlineData("0x", RuleErrorCode.InvalidLiteral)]
    [InlineData("\"unterminated", RuleErrorCode.Syntax)]
    [InlineData("\"new\nline\"", RuleErrorCode.Syntax)]
    [InlineData("\"bad \\q escape\"", RuleErrorCode.Syntax)]
    [InlineData("\"\\x4\"", RuleErrorCode.Syntax)]
    [InlineData("\"\\uD800\"", RuleErrorCode.Syntax)]
    [InlineData("\"\\U00110000\"", RuleErrorCode.Syntax)]
    [InlineData("\"\\08\"", RuleErrorCode.Syntax)]
    [InlineData("\"\\", RuleErrorCode.Syntax)]
    [InlineData("x = 1", RuleErrorCode.Syntax)]
    [InlineData("b & b", RuleErrorCode.Syntax)]
    [InlineData("b | b", RuleErrorCode.Syntax)]
    [InlineData("x # 1", RuleErrorCode.Syntax)]
    [InlineData("1 2", RuleErrorCode.Syntax)]
    [InlineData("", RuleErrorCode.Syntax)]
    [InlineData("   // only a comment", RuleErrorCode.Syntax)]
    [InlineData("(1 + 2", RuleErrorCode.Syntax)]
    [InlineData("[1, 2", RuleErrorCode.Syntax)]
    [InlineData("{\"a\" 1}", RuleErrorCode.Syntax)]
    [InlineData("true ? 1", RuleErrorCode.Syntax)]
    [InlineData("vehicle.", RuleErrorCode.Syntax)]
    [InlineData("ints[0", RuleErrorCode.Syntax)]
    [InlineData("size(ints", RuleErrorCode.Syntax)]
    [InlineData("if", RuleErrorCode.ReservedWord)]
    [InlineData("vehicle.while", RuleErrorCode.ReservedWord)]
    [InlineData("ints.all(1, true)", RuleErrorCode.Syntax)]
    [InlineData("ints.all(i)", RuleErrorCode.Syntax)]
    [InlineData("ints.map(i)", RuleErrorCode.Syntax)]
    [InlineData("ints.map(i, i, i, i)", RuleErrorCode.Syntax)]
    [InlineData("has(x)", RuleErrorCode.Syntax)]
    [InlineData("has(vehicle.value, x)", RuleErrorCode.Syntax)]
    [InlineData("Type{a: 1}", RuleErrorCode.Syntax)]
    public void Rejects_invalid_syntax(string source, RuleErrorCode code) => T.CompileError(source).Code.ShouldBe(code);

    [Fact]
    public void Syntax_errors_report_line_and_column()
    {
        var error = T.CompileError("x +\n  * 2");
        error.Code.ShouldBe(RuleErrorCode.Syntax);
        error.Position.ShouldBe(new SourcePosition(6, 2, 3));
        error.CodeText.ShouldBe("RULE-SYNTAX");
        error.ToString().ShouldStartWith("RULE-SYNTAX at 2:3: unexpected '*'");
    }

    [Fact]
    public void Expression_length_is_limited()
    {
        var env = RuleEnvironment.Create(T.Schema, new RuleLimits { MaxExpressionLength = 10 });
        T.CompileError("x + y + x + y", env: env).Code.ShouldBe(RuleErrorCode.ExpressionTooLong);
        env.TryCompile("x + y").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Nesting_depth_is_limited()
    {
        string deepParens = new string('(', 250) + "1" + new string(')', 250);
        T.CompileError(deepParens).Code.ShouldBe(RuleErrorCode.DepthExceeded);

        string longChain = string.Join(" + ", Enumerable.Repeat("1", 250));
        T.CompileError(longChain).Code.ShouldBe(RuleErrorCode.DepthExceeded);

        string deepUnary = new string('!', 250) + "true";
        T.CompileError(deepUnary).Code.ShouldBe(RuleErrorCode.DepthExceeded);

        string ok = string.Join(" + ", Enumerable.Repeat("1", 150));
        T.Eval(ok).ShouldBe("150");
    }

    [Fact]
    public void Compile_exception_lists_all_errors()
    {
        var ex = Should.Throw<RuleCompileException>(() => T.Env.Compile("unknown + 1"));
        ex.Errors.Single().Code.ShouldBe(RuleErrorCode.UnknownIdentifier);
        ex.Message.ShouldContain("RULE-UNKNOWN-IDENTIFIER");
        new RuleCompileException(Array.Empty<RuleCompileError>()).Message.ShouldBe("rule compilation failed");
    }

    [Fact]
    public void Every_error_code_has_a_stable_text_code()
    {
        var codes = Enum.GetValues<RuleErrorCode>().Select(c => c.ToCode()).ToList();
        codes.ShouldBeUnique();
        codes.ShouldAllBe(c => c.StartsWith("RULE-", StringComparison.Ordinal) || c.StartsWith("PLT-ERR-", StringComparison.Ordinal));
        RuleErrorCode.TableNotActive.ToCode().ShouldBe("PLT-ERR-TABLE-NOT-ACTIVE");
        Should.Throw<ArgumentOutOfRangeException>(() => ((RuleErrorCode)999).ToCode());
    }

    [Fact]
    public void Source_position_from_offset_clamps_and_counts_lines()
    {
        SourcePosition.FromOffset("ab\ncd", 4).ShouldBe(new SourcePosition(4, 2, 2));
        SourcePosition.FromOffset("ab", 99).ShouldBe(new SourcePosition(2, 1, 3));
        SourcePosition.FromOffset("ab", -5).ToString().ShouldBe("1:1");
    }
}
