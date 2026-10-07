using CoreIns.Rules.DecisionTables;

namespace CoreIns.Rules.Tests;

/// <summary>Regression tests for the two non-blocking follow-ups after the F-1f(c) review passed.</summary>
public class ReviewFollowUpTests
{
    private static readonly InputSchema Schema = InputSchema.Define()
        .Variable("d", RuleType.Dyn)
        .Variable("e", RuleType.Dyn)
        .Build();

    private static RuleValue Nested(int depth, int leaf = 1)
    {
        RuleValue v = leaf;
        for (int i = 0; i < depth; i++)
        {
            v = RuleValue.List(v);
        }

        return v;
    }

    private static RuleInputs Inputs(RuleValue d, RuleValue e) => Schema.NewInputs().Set("d", d).Set("e", e).Build();

    // ---------------------------------------------------------------- follow-up 1: deep values cannot crash the process

    [Fact]
    public void Deep_values_compare_iteratively_without_stack_overflow()
    {
        var a = Nested(19_000);
        var b = Nested(19_000);
        a.Equals(b).ShouldBeTrue();
        a.Equals(Nested(19_000, leaf: 2)).ShouldBeFalse();
        a.Equals(Nested(18_999)).ShouldBeFalse();
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ToString().ShouldEndWith("...");
    }

    [Fact]
    public void Inputs_nested_beyond_the_ceiling_are_refused_when_built()
    {
        Should.Throw<RuleInputException>(() => Schema.NewInputs().Set("d", Nested(19_000)))
            .Message.ShouldContain("nesting depth");
        Should.Throw<RuleInputException>(() => Schema.NewInputs().Set("d", Nested(RuleLimits.InputDepthCeiling)));
        Schema.NewInputs().Set("d", Nested(RuleLimits.InputDepthCeiling - 1)).Set("e", 1).Build()["d"].ShouldNotBeNull();
    }

    [Fact]
    public void Inputs_nested_beyond_MaxInputDepth_fail_evaluation_with_limit_exceeded()
    {
        var inputs = Inputs(Nested(100), Nested(100));
        var result = RuleEnvironment.Create(Schema).Compile("d == e").Evaluate(inputs);
        result.Error!.Code.ShouldBe(RuleErrorCode.LimitExceeded);
        result.Error.Message.ShouldContain("nested");

        var deepEnv = RuleEnvironment.Create(Schema, new RuleLimits { MaxInputDepth = 200 });
        deepEnv.Compile("d == e").Evaluate(inputs).Value.ShouldBe(BoolValue.True);
        deepEnv.Compile("d == e").Evaluate(Inputs(Nested(100), Nested(100, leaf: 2))).Value.ShouldBe(BoolValue.False);
    }

    [Fact]
    public void Deep_host_function_results_are_refused()
    {
        var deep = Nested(500);
        var host = new HostFunction("deep", Array.Empty<RuleType>(), RuleType.Dyn, _ => deep);
        var env = RuleEnvironment.Create(Schema, functions: new[] { host });
        env.Compile("deep() == d").Evaluate(Inputs(1, 1)).Error!.Code.ShouldBe(RuleErrorCode.LimitExceeded);
    }

    [Fact]
    public void Input_depth_limit_is_validated_and_part_of_the_hash()
    {
        Should.Throw<ArgumentException>(() => RuleEnvironment.Create(Schema, new RuleLimits { MaxInputDepth = 0 }));
        Should.Throw<ArgumentException>(() => RuleEnvironment.Create(Schema, new RuleLimits { MaxInputDepth = RuleLimits.InputDepthCeiling + 1 }));
        RuleLimits.Default.MaxInputDepth.ShouldBe(64);
        RuleLimits.Default.Fingerprint().ShouldContain("inputDepth=64");
        RuleEnvironment.Create(Schema, new RuleLimits { MaxInputDepth = 65 }).Compile("d == e").ContentHash
            .ShouldNotBe(RuleEnvironment.Create(Schema).Compile("d == e").ContentHash);
    }

    // ---------------------------------------------------------------- follow-up 2: aggregate decision-table output size

    [Fact]
    public void Collect_outputs_are_charged_against_the_allocation_budget()
    {
        var schema = InputSchema.Define().Variable("big", RuleType.ListOf(RuleType.Int)).Build();
        var env = RuleEnvironment.Create(schema, new RuleLimits { MaxEvaluationSteps = long.MaxValue / 2 });
        var rules = Enumerable.Range(0, 150)
            .Select(i => new DecisionRule("R" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Array.Empty<string>(), new[] { "big" }))
            .ToArray();
        var def = new DecisionTableDefinition(
            new DecisionTableMetadata("T", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.Collect,
            Array.Empty<InputColumn>(),
            new[] { new OutputColumn("o", RuleType.ListOf(RuleType.Int)) },
            rules);
        var table = CompiledDecisionTable.Compile(def, env);
        var inputs = schema.NewInputs().Set("big", RuleValue.List(Enumerable.Range(0, 899_999).Select(i => (RuleValue)i))).Build();
        var result = table.Evaluate(inputs, new DateOnly(2026, 6, 1));
        result.Error!.Code.ShouldBe(RuleErrorCode.LimitExceeded);
        result.Error.Context.ShouldBe("rule 'R1' output 'o'");
        result.Matches.ShouldBeEmpty();

        var single = CompiledDecisionTable.Compile(def with { HitPolicy = HitPolicy.First }, env).Evaluate(inputs, new DateOnly(2026, 6, 1));
        single.IsSuccess.ShouldBeTrue();
        single.Matches.Single().RuleId.ShouldBe("R0");
    }
}
