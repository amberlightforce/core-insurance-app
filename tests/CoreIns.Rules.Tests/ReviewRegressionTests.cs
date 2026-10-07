using System.Diagnostics;
using CoreIns.Rules.DecisionTables;

namespace CoreIns.Rules.Tests;

/// <summary>One regression test (or more) per defect found by the independent review of F-1f(c), attempt 1.</summary>
public class ReviewRegressionTests
{
    private static readonly InputSchema Schema = InputSchema.Define()
        .Variable("l", RuleType.ListOf(RuleType.Int))
        .Variable("big", RuleType.ListOf(RuleType.Int))
        .Variable("n", RuleType.Decimal, nullable: true)
        .Variable("p", RuleType.Int)
        .Build();

    private static readonly RuleInputs Inputs = Schema.NewInputs()
        .Set("l", RuleValue.List(Enumerable.Range(1, 10).Select(i => (RuleValue)i)))
        .Set("big", RuleValue.List(Enumerable.Range(1, 5000).Select(i => (RuleValue)i)))
        .Set("p", 100)
        .Build();

    private static readonly RuleEnvironment Env = RuleEnvironment.Create(Schema);

    private static (EvaluationResult Result, TimeSpan Elapsed) Timed(RuleEnvironment env, string source, EvaluationOptions? options = null)
    {
        var compiled = env.Compile(source);
        long start = Stopwatch.GetTimestamp();
        var result = compiled.Evaluate(Inputs, options);
        return (result, Stopwatch.GetElapsedTime(start));
    }

    private static readonly TimeSpan Fast = TimeSpan.FromTicks(100 * TimeSpan.TicksPerMillisecond);

    // ---------------------------------------------------------------- D1: the cost budget bounds work, memory and time

    /// <summary>E_k = [E_{k-1}].map(m_k, l.map(z_k, m_k))[0]: a value of weight 10^k built in linear steps.</summary>
    private static string Exponential(int k)
    {
        string e = "l";
        for (int i = 1; i <= k; i++)
        {
            e = $"[{e}].map(m{i}, l.map(z{i}, m{i}))[0]";
        }

        return e;
    }

    [Fact]
    public void D1_comparing_an_exponentially_shared_value_fails_fast_with_cost_exceeded()
    {
        string e9 = Exponential(9);
        var (result, elapsed) = Timed(Env, e9 + " == " + e9);
        result.Error!.Code.ShouldBe(RuleErrorCode.CostExceeded);
        elapsed.ShouldBeLessThan(Fast);
        Timed(Env, $"size({Exponential(3)})").Result.Value.ShouldBe(IntValue.Of(10));
    }

    [Fact]
    public void D1_building_large_intermediate_lists_fails_fast_with_a_resource_error()
    {
        var (result, elapsed) = Timed(Env, "big.map(x, big + big)");
        result.Error!.Code.ShouldBeOneOf(RuleErrorCode.CostExceeded, RuleErrorCode.LimitExceeded);
        elapsed.ShouldBeLessThan(Fast);
    }

    [Fact]
    public void D1_quadratic_membership_fails_fast_with_cost_exceeded()
    {
        var (result, elapsed) = Timed(Env, "big.all(x, x in big)");
        result.Error!.Code.ShouldBe(RuleErrorCode.CostExceeded);
        elapsed.ShouldBeLessThan(Fast);
        result.StepsUsed.ShouldBeLessThanOrEqualTo(RuleLimits.Default.MaxEvaluationSteps + 5000);
    }

    [Fact]
    public void D1_allocation_cap_is_enforced()
    {
        var env = RuleEnvironment.Create(Schema, new RuleLimits { MaxAllocatedElements = 100 });
        Timed(env, "big.map(x, x)").Result.Error!.Code.ShouldBe(RuleErrorCode.LimitExceeded);
        Timed(env, "l.map(x, x)").Result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void D1_wall_clock_deadline_is_enforced()
    {
        var env = RuleEnvironment.Create(Schema, new RuleLimits
        {
            MaxEvaluationSteps = long.MaxValue / 2,
            MaxEvaluationTime = TimeSpan.FromTicks(20 * TimeSpan.TicksPerMillisecond),
        });
        var (result, elapsed) = Timed(env, "big.all(x, x in big)");
        result.Error!.Code.ShouldBe(RuleErrorCode.Timeout);
        result.Error.CodeText.ShouldBe("RULE-TIMEOUT");
        elapsed.ShouldBeLessThan(TimeSpan.FromTicks(1000 * TimeSpan.TicksPerMillisecond));
    }

    [Fact]
    public void D1_cancellation_is_a_typed_error_and_is_never_absorbed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var options = new EvaluationOptions { Cancellation = cts.Token };
        Timed(Env, "big.all(x, x > 0)", options).Result.Error!.CodeText.ShouldBe("RULE-CANCELLED");
        Timed(Env, "big.all(x, x > 0) || true", options).Result.Error!.Code.ShouldBe(RuleErrorCode.Cancelled);
    }

    [Fact]
    public void D1_work_is_charged_by_operand_size()
    {
        Timed(Env, "l == l").Result.StepsUsed.ShouldBeGreaterThan(Timed(Env, "1 == 1").Result.StepsUsed + 5);
        Timed(Env, "5000 in big").Result.StepsUsed.ShouldBeGreaterThan(5000);
    }

    // ---------------------------------------------------------------- D2: the table canonical text is injective

    private static DecisionTableDefinition ConstantTable(params DecisionRule[] rules) => new(
        new DecisionTableMetadata("T", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
        HitPolicy.Collect,
        Array.Empty<InputColumn>(),
        new[] { new OutputColumn("o", RuleType.Int) },
        rules);

    [Fact]
    public void D2_rule_ids_cannot_forge_table_structure()
    {
        var a = CompiledDecisionTable.Compile(ConstantTable(
            new DecisionRule("R1", Array.Empty<string>(), new[] { "1" }),
            new DecisionRule("R2", Array.Empty<string>(), new[] { "2" })), Env);
        var b = CompiledDecisionTable.Compile(ConstantTable(
            new DecisionRule("R1 prio=0 => | cel-subset/1.0:(int 1)\nrule R2", Array.Empty<string>(), new[] { "2" })), Env);
        a.ContentHash.ShouldNotBe(b.ContentHash);
        a.CanonicalText.ShouldNotBe(b.CanonicalText);

        var c = CompiledDecisionTable.Compile(ConstantTable(new DecisionRule("A;1:B", Array.Empty<string>(), new[] { "1" })), Env);
        var d = CompiledDecisionTable.Compile(ConstantTable(new DecisionRule("A", Array.Empty<string>(), new[] { "1" })), Env);
        c.ContentHash.ShouldNotBe(d.ContentHash);
    }

    // ---------------------------------------------------------------- D3: hashes cover the schema and host signatures

    [Fact]
    public void D3_expression_hash_depends_on_input_types_and_host_signatures()
    {
        var asInt = RuleEnvironment.Create(InputSchema.Define().Variable("p", RuleType.Int).Build());
        var asDecimal = RuleEnvironment.Create(InputSchema.Define().Variable("p", RuleType.Decimal).Build());
        var i = asInt.Compile("p / 3");
        var d = asDecimal.Compile("p / 3");
        i.CanonicalText.ShouldBe(d.CanonicalText);
        i.ContentHash.ShouldNotBe(d.ContentHash);
        i.EvaluateOrThrow(asInt.Schema.NewInputs().Set("p", 100).Build()).ToString().ShouldBe("33");
        d.EvaluateOrThrow(asDecimal.Schema.NewInputs().Set("p", 100).Build()).ToString().ShouldBe("33.333333333333333333333333333");

        asInt.Compile("p", RuleType.Int).ContentHash.ShouldNotBe(asInt.Compile("p", RuleType.Decimal).ContentHash);
        asInt.Compile("p", RuleType.Int).ContentHash.ShouldNotBe(asInt.Compile("p", RuleType.Int.Nullable()).ContentHash);

        var f1 = new HostFunction("f", new[] { RuleType.Int }, RuleType.Int, a => a[0]);
        var f2 = new HostFunction("f", new[] { RuleType.Int }, RuleType.Decimal, a => a[0]);
        var unrelated = new HostFunction("g", Array.Empty<RuleType>(), RuleType.Int, _ => 1);
        var schema = InputSchema.Define().Variable("p", RuleType.Int).Build();
        string H(params HostFunction[] fs) => RuleEnvironment.Create(schema, functions: fs).Compile("f(p)").ContentHash;
        H(f1).ShouldNotBe(H(f2));
        H(f1).ShouldBe(H(f1, unrelated));
    }

    [Fact]
    public void D3_table_hash_depends_on_input_types()
    {
        DecisionTableDefinition Def() => new(
            new DecisionTableMetadata("T", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.First,
            Array.Empty<InputColumn>(),
            new[] { new OutputColumn("o", RuleType.Decimal) },
            new[] { new DecisionRule("R", Array.Empty<string>(), new[] { "p / 3" }) });
        var asInt = RuleEnvironment.Create(InputSchema.Define().Variable("p", RuleType.Int).Build());
        var asDecimal = RuleEnvironment.Create(InputSchema.Define().Variable("p", RuleType.Decimal).Build());
        var ti = CompiledDecisionTable.Compile(Def(), asInt);
        var td = CompiledDecisionTable.Compile(Def(), asDecimal);
        ti.ContentHash.ShouldNotBe(td.ContentHash);
        ti.Evaluate(asInt.Schema.NewInputs().Set("p", 100).Build(), new DateOnly(2026, 6, 1)).Match!.Output("o").ToString().ShouldBe("33");
        td.Evaluate(asDecimal.Schema.NewInputs().Set("p", 100).Build(), new DateOnly(2026, 6, 1)).Match!.Output("o").ToString()
            .ShouldBe("33.333333333333333333333333333");
    }

    // ---------------------------------------------------------------- D5: resource errors on the right are not masked

    [Fact]
    public void D5_non_absorbable_right_error_wins_over_absorbed_left_error()
    {
        var env = RuleEnvironment.Create(Schema, new RuleLimits { MaxEvaluationSteps = 500 });
        Timed(env, "1 / 0 == 1 || big.exists(x, x < 0)").Result.Error!.Code.ShouldBe(RuleErrorCode.CostExceeded);
        Timed(env, "1 / 0 == 1 && big.all(x, x > 0)").Result.Error!.Code.ShouldBe(RuleErrorCode.CostExceeded);
        Timed(env, "1 / 0 == 1 || 2 / 0 == 1").Result.Error!.Code.ShouldBe(RuleErrorCode.DivisionByZero);
    }

    // ---------------------------------------------------------------- D6: no silent precision loss

    [Theory]
    [InlineData("0.1234567890123456789012345678 * 3.3")]
    [InlineData("79228162514264337593543950.33 + 0.009")]
    [InlineData("79228162514264337593543950.33 - -0.009")]
    [InlineData("sum([79228162514264337593543950.33, 0.009])")]
    public void D6_results_needing_more_than_28_digits_fail_with_precision_loss(string source)
    {
        var error = Timed(Env, source).Result.Error!;
        error.Code.ShouldBe(RuleErrorCode.PrecisionLoss);
        error.CodeText.ShouldBe("RULE-PRECISION-LOSS");
    }

    [Theory]
    [InlineData("0.1000000000000000000000000000 * 10.00", "1.0000000000000000000000000000")]
    [InlineData("0.5 + 0.25", "0.75")]
    [InlineData("1 / 3.0", "0.3333333333333333333333333333")]
    [InlineData("round(1 / 3.0, 2, 'HalfUp') * 3", "0.99")]
    public void D6_exact_results_and_documented_division_still_work(string source, string expected) =>
        Timed(Env, source).Result.GetValueOrThrow().ToString().ShouldBe(expected);

    // ---------------------------------------------------------------- D7: expected result types are non-null by default

    [Fact]
    public void D7_null_result_for_a_non_nullable_expected_type_is_an_error()
    {
        Env.Compile("n", RuleType.Decimal).Evaluate(Inputs).Error!.Code.ShouldBe(RuleErrorCode.NullValue);
        Env.Compile("n", RuleType.Decimal.Nullable()).Evaluate(Inputs).Value.ShouldBe(RuleValue.Null);
        Env.TryCompile("null", RuleType.Decimal).Errors.Single().Code.ShouldBe(RuleErrorCode.ResultTypeMismatch);
        Env.Compile("null", RuleType.Decimal.Nullable()).Evaluate(Inputs).Value.ShouldBe(RuleValue.Null);
        Env.Compile("n").Evaluate(Inputs).Value.ShouldBe(RuleValue.Null);
        RuleType.Decimal.Nullable().ToString().ShouldBe("decimal?");
        RuleType.Decimal.Nullable().ShouldBe(RuleType.Decimal);
        RuleType.Decimal.Nullable().NonNullable().IsNullable.ShouldBeFalse();
        RuleType.Decimal.IsNullable.ShouldBeFalse();
    }

    [Fact]
    public void D7_table_outputs_and_columns_are_non_null_unless_declared()
    {
        DecisionTableDefinition Def(RuleType outputType) => new(
            new DecisionTableMetadata("T", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.First,
            Array.Empty<InputColumn>(),
            new[] { new OutputColumn("o", outputType) },
            new[] { new DecisionRule("R", Array.Empty<string>(), new[] { "n" }) });
        CompiledDecisionTable.Compile(Def(RuleType.Decimal), Env).Evaluate(Inputs, new DateOnly(2026, 6, 1)).Error!.Code.ShouldBe(RuleErrorCode.NullValue);
        CompiledDecisionTable.Compile(Def(RuleType.Decimal.Nullable()), Env).Evaluate(Inputs, new DateOnly(2026, 6, 1)).Match!.Output("o").ShouldBe(RuleValue.Null);
    }

    // ---------------------------------------------------------------- D8: error positions

    [Theory]
    [InlineData("p + 'a'", 3)]
    [InlineData("[1] + 'a'", 5)]
    [InlineData("l[0] < 'x'", 6)]
    [InlineData("p == 'x'", 3)]
    [InlineData("p.foo()", 3)]
    [InlineData("l['k']", 2)]
    [InlineData("p && true", 1)]
    [InlineData("l.startsWith('a')", 1)]
    [InlineData("p ? 1 : 2", 1)]
    public void D8_type_errors_point_at_the_operator_or_function(string source, int column) =>
        Env.TryCompile(source).Errors.Single().Position!.Value.Column.ShouldBe(column);

    [Fact]
    public void D8_cell_errors_point_into_the_cell_text()
    {
        var def = new DecisionTableDefinition(
            new DecisionTableMetadata("T", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.First,
            new[] { new InputColumn("c", RuleType.Int, "p") },
            new[] { new OutputColumn("o", RuleType.Int) },
            new[]
            {
                new DecisionRule("R1", new[] { "  < unknownX" }, new[] { "1" }),
                new DecisionRule("R2", new[] { "[1..zz)" }, new[] { "1" }),
                new DecisionRule("R3", new[] { "in [1, 'a']" }, new[] { "1" }),
            });
        var errors = Should.Throw<RuleCompileException>(() => CompiledDecisionTable.Compile(def, Env)).Errors;
        errors[0].Position.ShouldBe(new SourcePosition(4, 1, 5));
        errors[1].Position.ShouldBe(new SourcePosition(4, 1, 5));
        errors[2].Position!.Value.Offset.ShouldBe(7);
    }

    // ---------------------------------------------------------------- D9: null column semantics

    [Theory]
    [InlineData("in [1, 2]", false)]
    [InlineData("in [null, 1]", false)]
    [InlineData("not in [1, 2]", true)]
    [InlineData("!= 1", true)]
    [InlineData("== 1", false)]
    [InlineData("1", false)]
    [InlineData("null", true)]
    [InlineData("not null", false)]
    public void D9_null_column_matches_only_negative_tests_and_null(string cell, bool matches)
    {
        var def = new DecisionTableDefinition(
            new DecisionTableMetadata("T", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.First,
            new[] { new InputColumn("c", RuleType.Decimal.Nullable(), "n") },
            new[] { new OutputColumn("o", RuleType.Int) },
            new[] { new DecisionRule("R", new[] { cell }, new[] { "1" }) });
        CompiledDecisionTable.Compile(def, Env).Evaluate(Inputs, new DateOnly(2026, 6, 1)).Matches.Count.ShouldBe(matches ? 1 : 0);
    }

    // ---------------------------------------------------------------- D10: duration resolution is 100 ns

    [Fact]
    public void D10_durations_have_100ns_resolution()
    {
        Env.Compile("duration('100ns')").EvaluateOrThrow(Inputs).ToString().ShouldBe("duration(\"0.0000001s\")");
        Env.Compile("duration('1.5us')").EvaluateOrThrow(Inputs).ToString().ShouldBe("duration(\"0.0000015s\")");
        Env.TryCompile("duration('150ns')").Errors.Single().Code.ShouldBe(RuleErrorCode.InvalidLiteral);
        Env.TryCompile("duration('1ns')").Errors.Single().Message.ShouldContain("duration");
    }

    // ---------------------------------------------------------------- D11: deterministic cost of a 150-rule table

    [Fact]
    public void D11_a_150_rule_table_evaluates_within_a_fixed_step_count()
    {
        var rules = Enumerable.Range(0, 150)
            .Select(i => new DecisionRule("R" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), new[] { $"[{i}..{i + 1})" }, new[] { "1" }))
            .ToArray();
        var def = new DecisionTableDefinition(
            new DecisionTableMetadata("T", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.Collect,
            new[] { new InputColumn("c", RuleType.Int, "p + 49") },
            new[] { new OutputColumn("o", RuleType.Int) },
            rules);
        var result = CompiledDecisionTable.Compile(def, Env).Evaluate(Inputs, new DateOnly(2026, 6, 1));
        result.MatchedRuleIds.ShouldBe(new[] { "R149" });
        result.Trace.StepsUsed.ShouldBeLessThan(5_000);
    }

    // ---------------------------------------------------------------- D12: MaxAstDepth is capped

    [Fact]
    public void D12_max_ast_depth_is_capped()
    {
        Should.Throw<ArgumentException>(() => RuleEnvironment.Create(Schema, new RuleLimits { MaxAstDepth = RuleLimits.AstDepthCeiling + 1 }));
        RuleEnvironment.Create(Schema, new RuleLimits { MaxAstDepth = RuleLimits.AstDepthCeiling }).Limits.MaxAstDepth.ShouldBe(1_000);
        Should.Throw<ArgumentException>(() => RuleEnvironment.Create(Schema, new RuleLimits { MaxAllocatedElements = 0 }));
        Should.Throw<ArgumentException>(() => RuleEnvironment.Create(Schema, new RuleLimits { MaxEvaluationTime = TimeSpan.Zero }));
    }
}
