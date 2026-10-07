using System.Diagnostics;
using CoreIns.Rules.DecisionTables;

namespace CoreIns.Rules.Tests;

/// <summary>Regression tests for the findings of the second independent review of F-1f(c) (N1, M1–M4).</summary>
public class ReviewAttempt2Tests
{
    private static readonly InputSchema Schema = InputSchema.Define()
        .Variable("l", RuleType.ListOf(RuleType.Int))
        .Variable("big", RuleType.ListOf(RuleType.Int))
        .Variable("s", RuleType.String)
        .Variable("names", RuleType.MapOf(RuleType.String, RuleType.Int))
        .Variable("birth", RuleType.Date)
        .Build();

    private static RuleInputs MakeInputs(string s = "x") => Schema.NewInputs()
        .Set("l", RuleValue.List(Enumerable.Range(1, 10).Select(i => (RuleValue)i)))
        .Set("big", RuleValue.List(Enumerable.Range(1, 5000).Select(i => (RuleValue)i)))
        .Set("s", s)
        .Set("names", RuleValue.Map(new[] { new KeyValuePair<RuleValue, RuleValue>("a", 1) }))
        .Set("birth", new DateOnly(1980, 3, 14))
        .Build();

    private static readonly RuleInputs Inputs = MakeInputs();

    private static readonly RuleEnvironment Env = RuleEnvironment.Create(Schema);

    private static readonly TimeSpan Fast = TimeSpan.FromTicks(100 * TimeSpan.TicksPerMillisecond);

    /// <summary>The reviewer's construction: l wrapped k times in [...].map(m, l.map(z, m))[0] (weight 10^k).</summary>
    private static string Wrapped(int k)
    {
        string e = "l";
        for (int i = 1; i <= k; i++)
        {
            e = $"[{e}].map(m{i}, l.map(z{i}, m{i}))[0]";
        }

        return e;
    }

    /// <summary>The same shape built directly by a caller: a list of 10 references to the previous level.</summary>
    private static RuleValue CallerBuilt(int k, int leaf = 1)
    {
        RuleValue v = RuleValue.List(Enumerable.Range(1, 10).Select(i => (RuleValue)(i == 10 ? leaf : i)));
        for (int i = 0; i < k; i++)
        {
            v = RuleValue.List(Enumerable.Repeat(v, 10));
        }

        return v;
    }

    /// <summary>Runs the action twice and returns the faster run (excludes JIT warm-up from the bound).</summary>
    private static TimeSpan Time(Action action)
    {
        var best = TimeSpan.MaxValue;
        for (int i = 0; i < 2; i++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            var elapsed = Stopwatch.GetElapsedTime(start);
            best = elapsed < best ? elapsed : best;
        }

        return best;
    }

    // ---------------------------------------------------------------- N1

    [Theory]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(25)]
    public void N1_heavy_shared_results_fail_fast_with_a_typed_error(int k)
    {
        var compiled = Env.Compile(Wrapped(k), RuleType.ListOf(RuleType.Dyn));
        EvaluationResult? plain = null, traced = null;
        Time(() =>
        {
            plain = compiled.Evaluate(Inputs);
            traced = compiled.Evaluate(Inputs, new EvaluationOptions { Trace = true });
        }).ShouldBeLessThan(Fast);
        plain!.Error!.Code.ShouldBeOneOf(RuleErrorCode.CostExceeded, RuleErrorCode.LimitExceeded);
        traced!.Error!.Code.ShouldBeOneOf(RuleErrorCode.CostExceeded, RuleErrorCode.LimitExceeded);
        plain.Value.ShouldBeNull();
        plain.StepsUsed.ShouldBeLessThanOrEqualTo(RuleLimits.Default.MaxEvaluationSteps + 1);
        Time(() =>
        {
            _ = traced.Trace.Sum(t => (long)t.ToString().Length);
            _ = traced.Trace.Sum(t => (long)t.Value.GetHashCode());
            traced.Trace.All(t => t.Value.Equals(t.Value)).ShouldBeTrue();
        }).ShouldBeLessThan(Fast);
    }

    [Fact]
    public void N1_heavy_decision_table_output_fails_fast()
    {
        var def = new DecisionTableDefinition(
            new DecisionTableMetadata("T", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.First,
            Array.Empty<InputColumn>(),
            new[] { new OutputColumn("o", RuleType.ListOf(RuleType.Dyn)) },
            new[] { new DecisionRule("R", Array.Empty<string>(), new[] { Wrapped(20) }) });
        var table = CompiledDecisionTable.Compile(def, Env);
        DecisionResult? result = null;
        Time(() => result = table.Evaluate(Inputs, new DateOnly(2026, 6, 1), new DecisionEvaluationOptions { DetailedTrace = true }))
            .ShouldBeLessThan(Fast);
        result!.Error!.Code.ShouldBeOneOf(RuleErrorCode.CostExceeded, RuleErrorCode.LimitExceeded);
        result.Matches.ShouldBeEmpty();
    }

    [Fact]
    public void N1_activation_gate_compares_heavy_expected_values_in_bounded_time()
    {
        var def = new DecisionTableDefinition(
            new DecisionTableMetadata("T", "1", DecisionTableStatus.Draft, new DateOnly(2026, 1, 1)),
            HitPolicy.First,
            Array.Empty<InputColumn>(),
            new[] { new OutputColumn("o", RuleType.ListOf(RuleType.Dyn)) },
            new[] { new DecisionRule("R", Array.Empty<string>(), new[] { "l" }) });
        var table = CompiledDecisionTable.Compile(def, Env);
        var heavy = CallerBuilt(25);
        IReadOnlyList<DecisionTableTestOutcome>? outcomes = null;
        Time(() => outcomes = table.RunTests(new[]
        {
            new DecisionTableTestCase("heavy expectation", Inputs, new[] { "R" }) { ExpectedOutputs = new[] { new[] { new NamedValue("o", heavy) } } },
        })).ShouldBeLessThan(Fast);
        outcomes!.Single().Passed.ShouldBeFalse();
        outcomes!.Single().Failure!.ShouldContain("heavier than the allocation budget");
    }

    [Fact]
    public void N1_values_built_outside_the_engine_are_safe_to_render_hash_compare_and_refused_as_inputs()
    {
        var a = CallerBuilt(25);
        var b = CallerBuilt(25);
        var c = CallerBuilt(25, leaf: 99);
        bool same = false, different = true;
        string text = string.Empty;
        Time(() =>
        {
            text = a.ToString();
            a.GetHashCode().ShouldBe(b.GetHashCode());
            same = a.Equals(b);
            different = a.Equals(c);
        }).ShouldBeLessThan(Fast);
        same.ShouldBeTrue();
        different.ShouldBeFalse();
        text.Length.ShouldBeLessThanOrEqualTo(4096 + 3);
        text.ShouldEndWith("...");
        Should.Throw<RuleInputException>(() => Schema.NewInputs().Set("l", a)).Message.ShouldContain("weight");
        new StringValue(new string('x', 100_000)).ToString().Length.ShouldBeLessThanOrEqualTo(4096 + 5);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    public void N1_doubling_by_list_literals_fails_fast(int k)
    {
        string e = "l";
        for (int i = 0; i < k; i++)
        {
            e = $"[{e}].map(q{i}, [q{i}, q{i}])";
        }

        EvaluationResult? result = null;
        var compiled = Env.Compile("size(" + e + ") > 0");
        Time(() => result = compiled.Evaluate(Inputs, new EvaluationOptions { Trace = true })).ShouldBeLessThan(Fast);
        result!.Error!.Code.ShouldBeOneOf(RuleErrorCode.CostExceeded, RuleErrorCode.LimitExceeded);
    }

    [Fact]
    public void N1_heavy_host_function_results_are_refused()
    {
        var heavy = CallerBuilt(25);
        var host = new HostFunction("heavy", Array.Empty<RuleType>(), RuleType.Dyn, _ => heavy);
        var env = RuleEnvironment.Create(Schema, functions: new[] { host });
        EvaluationResult? result = null;
        Time(() => result = env.Compile("heavy()").Evaluate(Inputs, new EvaluationOptions { Trace = true })).ShouldBeLessThan(Fast);
        result!.Error!.Code.ShouldBeOneOf(RuleErrorCode.CostExceeded, RuleErrorCode.LimitExceeded);
    }

    [Fact]
    public void N1_comprehensions_charge_the_weight_of_produced_elements()
    {
        Env.Compile("l.map(x, l)").Evaluate(Inputs).StepsUsed.ShouldBeGreaterThan(100);
        Env.Compile("[l, l].filter(x, true)").Evaluate(Inputs).StepsUsed.ShouldBeGreaterThan(40);
    }

    [Fact]
    public void N1_trace_elides_heavy_values()
    {
        var result = Env.Compile("big.map(x, x)").Evaluate(Inputs, new EvaluationOptions { Trace = true });
        result.IsSuccess.ShouldBeTrue();
        var elided = result.Trace.Select(t => t.Value).OfType<ElidedValue>().ToList();
        elided.ShouldNotBeEmpty();
        elided.ShouldContain(e => e.OriginalKind == RuleTypeKind.List && e.OriginalWeight == 5001);
        elided[0].ToString().ShouldBe("<List elided: weight 5001>");
        elided[0].Kind.ShouldBe(RuleTypeKind.Dyn);
        elided[0].Equals(elided[0]).ShouldBeTrue();
        elided[0].GetHashCode().ShouldBe(elided[0].GetHashCode());
        Env.Compile("l").Evaluate(Inputs, new EvaluationOptions { Trace = true }).Trace.Single().Value.ShouldBeOfType<ListValue>();
    }

    // ---------------------------------------------------------------- M1

    [Fact]
    public void M1_lone_surrogates_are_rejected()
    {
        Env.TryCompile("s == '\uD800'").Errors.Single().Code.ShouldBe(RuleErrorCode.InvalidLiteral);
        Env.TryCompile("s == '\uDC00'").Errors.Single().Code.ShouldBe(RuleErrorCode.InvalidLiteral);
        Env.TryCompile("s == 'a\uD800b'").Errors.Single().Code.ShouldBe(RuleErrorCode.InvalidLiteral);
        Env.Compile("s == '😀'").ContentHash.ShouldNotBe(Env.Compile("s == '�'").ContentHash);

        DecisionTableDefinition Def(string ruleId) => new(
            new DecisionTableMetadata("T", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.First,
            Array.Empty<InputColumn>(),
            new[] { new OutputColumn("o", RuleType.Int) },
            new[] { new DecisionRule(ruleId, Array.Empty<string>(), new[] { "1" }) });
        Should.Throw<RuleCompileException>(() => CompiledDecisionTable.Compile(Def("R\uD800"), Env)).Errors.Single().Code.ShouldBe(RuleErrorCode.InvalidLiteral);
        Should.Throw<RuleCompileException>(() => CompiledDecisionTable.Compile(Def("R\uDC00"), Env)).Errors.Single().Code.ShouldBe(RuleErrorCode.InvalidLiteral);
        CompiledDecisionTable.Compile(Def("R😀"), Env).ContentHash.ShouldNotBe(CompiledDecisionTable.Compile(Def("R�"), Env).ContentHash);
    }

    // ---------------------------------------------------------------- M2

    [Fact]
    public void M2_limits_are_part_of_the_content_hash()
    {
        var a = RuleEnvironment.Create(Schema, new RuleLimits { MaxEvaluationSteps = 1_000 });
        var b = RuleEnvironment.Create(Schema, new RuleLimits { MaxEvaluationSteps = 2_000 });
        var c = RuleEnvironment.Create(Schema, new RuleLimits { MaxEvaluationSteps = 1_000 });
        a.Compile("size(l)").ContentHash.ShouldNotBe(b.Compile("size(l)").ContentHash);
        a.Compile("size(l)").ContentHash.ShouldBe(c.Compile("size(l)").ContentHash);
        a.Compile("size(l)").EnvironmentFingerprint.ShouldContain("steps=1000;");
        foreach (var limits in new[]
        {
            new RuleLimits { MaxAllocatedElements = 7 }, new RuleLimits { MaxEvaluationTime = TimeSpan.FromTicks(7) },
            new RuleLimits { MaxAstDepth = 7 }, new RuleLimits { MaxCollectionSize = 7 }, new RuleLimits { MaxStringLength = 7 },
            new RuleLimits { MaxRegexPatternLength = 7 }, new RuleLimits { RegexTimeout = TimeSpan.FromTicks(7) },
            new RuleLimits { MaxTraceEntries = 7 }, new RuleLimits { MaxTraceValueWeight = 7 }, new RuleLimits { MaxExpressionLength = 70 },
        })
        {
            limits.Fingerprint().ShouldNotBe(RuleLimits.Default.Fingerprint());
        }
    }

    // ---------------------------------------------------------------- M3

    [Fact]
    public void M3_steps_used_is_capped_at_budget_plus_one()
    {
        string e = Wrapped(3);
        var result = RuleEnvironment.Create(Schema, new RuleLimits { MaxEvaluationSteps = 50 })
            .Compile("big == big").Evaluate(Inputs);
        result.Error!.Code.ShouldBe(RuleErrorCode.CostExceeded);
        result.StepsUsed.ShouldBe(51);
        result.AttemptedSteps.ShouldBeGreaterThan(5000);
        Env.Compile(e).Evaluate(Inputs).AttemptedSteps.ShouldBe(Env.Compile(e).Evaluate(Inputs).StepsUsed);
    }

    // ---------------------------------------------------------------- M4

    [Fact]
    public void M4_errors_never_echo_input_content()
    {
        string secret = "GR" + new string('7', 1_500_000);
        var inputs = MakeInputs(secret);
        foreach (var source in new[] { "int(s)", "decimal(s)", "date(s)", "timestamp(s)", "duration(s)", "names[s]", "{s: 1, s: 2}" })
        {
            var error = Env.Compile(source).Evaluate(inputs).Error!;
            error.Message.Length.ShouldBeLessThan(200, source);
            error.Message.ShouldNotContain("GR777", Case.Sensitive, source);
        }

        Env.Compile("int(s)").Evaluate(inputs).Error!.Message.ShouldContain("a string of length 1500002");
        var age = Env.Compile("ageAt(birth, date('1970-01-01'))").Evaluate(inputs).Error!;
        age.Message.ShouldNotContain("1980");
        Env.TryCompile("date('" + new string('9', 500) + "')").Errors.Single().Message.Length.ShouldBeLessThan(200);
    }
}
