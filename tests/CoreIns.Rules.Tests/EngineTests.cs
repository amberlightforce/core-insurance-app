using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

/// <summary>Trace, content hash, cache, thread safety, limits, culture invariance.</summary>
public class EngineTests
{
    [Fact]
    public void Trace_records_each_sub_expression_and_its_value_in_evaluation_order()
    {
        var result = T.Run("x + y * 2", options: new EvaluationOptions { Trace = true });
        result.Value!.ToString().ShouldBe("11");
        result.Trace.Select(e => e.ToString()).ShouldBe(new[] { "x = 7", "y = 2", "y * 2 = 4", "x + y * 2 = 11" });
        result.Trace[2].Position.ShouldBe(new SourcePosition(4, 1, 5));
        result.TraceTruncated.ShouldBeFalse();
        result.StepsUsed.ShouldBe(5);
    }

    [Fact]
    public void Trace_omits_short_circuited_operands()
    {
        var result = T.Run("x < 0 && y > 100", options: new EvaluationOptions { Trace = true });
        result.Value.ShouldBe(BoolValue.False);
        result.Trace.Select(e => e.Expression).ShouldBe(new[] { "x", "x < 0", "x < 0 && y > 100" });
    }

    [Fact]
    public void Trace_is_kept_up_to_a_failure()
    {
        var result = T.Run("x + 1 > 0 && d / (x - 7) > 1.0", options: new EvaluationOptions { Trace = true });
        result.IsSuccess.ShouldBeFalse();
        result.Trace.Select(e => e.ToString()).ShouldContain("x - 7 = 0");
    }

    [Fact]
    public void Trace_can_be_truncated_by_limit_and_is_off_by_default()
    {
        var env = RuleEnvironment.Create(T.Schema, new RuleLimits { MaxTraceEntries = 2 });
        var result = env.Compile("x + y + x").Evaluate(T.Default, new EvaluationOptions { Trace = true });
        result.Trace.Count.ShouldBe(2);
        result.TraceTruncated.ShouldBeTrue();
        T.Run("x + y").Trace.ShouldBeEmpty();
    }

    [Fact]
    public void Comprehension_trace_shows_each_iteration()
    {
        var result = T.Run("ints.exists(i, i == 2)", options: new EvaluationOptions { Trace = true });
        result.Trace.Where(e => e.Expression == "i == 2").Select(e => e.Value.ToString()).ShouldBe(new[] { "false", "true" });
    }

    [Fact]
    public void Content_hash_is_sha256_of_canonical_text_and_ignores_whitespace_and_comments()
    {
        var a = T.Env.Compile("x + 1");
        a.CanonicalText.ShouldBe("cel-subset/1.0:(call _+_ (id x) (int 1))");
        a.ContentHash.ShouldBe(string.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(a.CanonicalText)).Select(x => x.ToString("x2", CultureInfo.InvariantCulture))));
        a.ContentHash.Length.ShouldBe(64);

        T.Env.Compile("  x+1 // add one\n").ContentHash.ShouldBe(a.ContentHash);
        T.Env.Compile("(x) + (1)").ContentHash.ShouldBe(a.ContentHash);
        T.Env.Compile("x + 2").ContentHash.ShouldNotBe(a.ContentHash);
        T.Env.Compile("1.0").ContentHash.ShouldNotBe(T.Env.Compile("1.00").ContentHash);
        T.Env.Compile("1").ContentHash.ShouldNotBe(T.Env.Compile("1.0").ContentHash);
        T.Env.Compile("'a'").ContentHash.ShouldBe(T.Env.Compile("\"a\"").ContentHash);
        T.Env.Compile("0x10").ContentHash.ShouldBe(T.Env.Compile("16").ContentHash);
    }

    [Fact]
    public void Canonical_text_covers_every_node_kind()
    {
        var c = T.Env.Compile(
            "has(vehicle.trackerFitted) || [\"a\\n\" == \"b\", true, null].size() > 0 && {\"k\": 1.5}[\"k\"] > 0.0 && ints.map(i, i > 1, i).all(j, j > 0) && mkt.round(d, \"x\") > 0.0");
        c.CanonicalText.ShouldBe(
            "cel-subset/1.0:(call _||_ (has (sel (id vehicle) trackerFitted)) (call _&&_ (call _&&_ (call _&&_ "
            + "(call _>_ (mcall size (list (call _==_ (str \"a\\n\") (str \"b\")) (bool true) (null))) (int 0)) "
            + "(call _>_ (call _[_] (map ((str \"k\") (dec 1.5))) (str \"k\")) (dec 0.0))) "
            + "(macro all j (macro map i (id ints) (call _>_ (id i) (int 1)) (id i)) (call _>_ (id j) (int 0)))) "
            + "(call _>_ (mcall round (id mkt) (id d) (str \"x\")) (dec 0.0))))");
    }

    [Fact]
    public void Compiled_expression_exposes_metadata()
    {
        var c = T.Env.Compile("d * 2", RuleType.Decimal);
        c.Source.ShouldBe("d * 2");
        c.ToString().ShouldBe("d * 2");
        c.ResultType.ShouldBe(RuleType.Decimal);
        c.Environment.ShouldBeSameAs(T.Env);
        T.Env.Schema.ShouldBeSameAs(T.Schema);
        T.Env.Limits.ShouldBe(RuleLimits.Default);
    }

    [Fact]
    public void Cache_returns_the_same_compiled_instance()
    {
        var env = RuleEnvironment.Create(T.Schema);
        var a = env.GetOrCompile("x + y");
        env.GetOrCompile("x + y").ShouldBeSameAs(a);
        env.GetOrCompile("x + y", RuleType.Decimal).ShouldNotBeSameAs(a);
        env.CachedCount.ShouldBe(2);
        Should.Throw<RuleCompileException>(() => env.GetOrCompile("nope"));
        Should.Throw<RuleCompileException>(() => env.GetOrCompile("nope"));
    }

    [Fact]
    public void Concurrent_compilation_and_evaluation_is_thread_safe_and_deterministic()
    {
        var env = RuleEnvironment.Create(T.Schema);
        const string source = "round(d * decimal(x) / 3.0 + sum(ints.map(i, i * x)), 2, \"HalfEven\")";
        var expected = new Dictionary<int, string>();
        for (int k = 0; k < 200; k++)
        {
            expected[k] = env.Compile(source).Evaluate(T.Inputs().Set("x", k).Build()).GetValueOrThrow().ToString();
        }

        var compiled = new System.Collections.Concurrent.ConcurrentBag<CompiledExpression>();
        var mismatches = 0;
        Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            int k = i % 200;
            var c = env.GetOrCompile(source);
            compiled.Add(c);
            var value = c.Evaluate(T.Inputs().Set("x", k).Build()).GetValueOrThrow().ToString();
            if (value != expected[k])
            {
                Interlocked.Increment(ref mismatches);
            }
        });

        mismatches.ShouldBe(0);
        compiled.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void Cost_budget_stops_runaway_rules_and_is_never_absorbed()
    {
        var env = RuleEnvironment.Create(T.Schema, new RuleLimits { MaxEvaluationSteps = 200 });
        const string heavy = "[1,2,3,4,5,6,7,8,9,10].map(i, [1,2,3,4,5,6,7,8,9,10].map(j, i * j))";
        var result = env.Compile(heavy).Evaluate(T.Default);
        result.Error!.Code.ShouldBe(RuleErrorCode.CostExceeded);
        result.StepsUsed.ShouldBe(201);

        env.Compile("size(" + heavy + ") > 0 || true").Evaluate(T.Default).Error!.Code.ShouldBe(RuleErrorCode.CostExceeded);
        env.Compile("ints.exists(i, size(" + heavy + ") > 0) || true").Evaluate(T.Default).Error!.Code.ShouldBe(RuleErrorCode.CostExceeded);

        T.Env.Compile("x + y + x").Evaluate(T.Default, new EvaluationOptions { MaxSteps = 3 }).Error!.Code.ShouldBe(RuleErrorCode.CostExceeded);
        T.Env.Compile("x + y").Evaluate(T.Default, new EvaluationOptions { MaxSteps = long.MaxValue }).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Collection_and_string_sizes_are_limited()
    {
        var env = RuleEnvironment.Create(T.Schema, new RuleLimits { MaxCollectionSize = 4, MaxStringLength = 15 });
        env.Compile("ints + ints").Evaluate(T.Default).Error!.Code.ShouldBe(RuleErrorCode.LimitExceeded);
        env.Compile("s + s").Evaluate(T.Default).Error!.Code.ShouldBe(RuleErrorCode.LimitExceeded);
        env.Compile("s + \"!\"").Evaluate(T.Default).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Invalid_limits_are_rejected()
    {
        Should.Throw<ArgumentException>(() => RuleEnvironment.Create(T.Schema, new RuleLimits { MaxEvaluationSteps = 0 }));
        Should.Throw<ArgumentException>(() => RuleEnvironment.Create(T.Schema, new RuleLimits { RegexTimeout = TimeSpan.Zero }));
    }

    [Fact]
    public void Regex_engine_is_linear_time_on_pathological_patterns()
    {
        string evil = new string('a', 5000) + "!";
        var schema = InputSchema.Define().Variable("t", RuleType.String).Build();
        var env = RuleEnvironment.Create(schema);
        var result = env.Compile("t.matches(\"^(a+)+$\")").Evaluate(schema.NewInputs().Set("t", evil).Build());
        result.Value.ShouldBe(BoolValue.False);
    }

    [Theory]
    [InlineData("el-GR")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void Parsing_and_formatting_are_culture_invariant(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            T.Eval("1.5 + 1000.25").ShouldBe("1001.75");
            T.Eval("string(1234.5)").ShouldBe("\"1234.5\"");
            T.Eval("decimal(\"1.25\")").ShouldBe("1.25");
            T.Eval("int(\"-42\")").ShouldBe("-42");
            T.Eval("\"TITLE\".lowerAscii()").ShouldBe("\"title\"");
            T.Eval("\"i\".upperAscii()").ShouldBe("\"I\"");
            T.Eval("string(date(\"2026-11-01\"))").ShouldBe("\"2026-11-01\"");
            T.Eval("string(timestamp(\"2026-11-01T10:00:00Z\"))").ShouldBe("\"2026-11-01T10:00:00Z\"");
            T.Eval("\"b\" > \"a\"").ShouldBe("true");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = savedUi;
        }
    }
}
