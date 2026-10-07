using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CoreIns.Rules.DecisionTables;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

public class DecisionTableTests
{
    private static readonly InputSchema Schema = InputSchema.Define()
        .Variable("age", RuleType.Int, nullable: true)
        .Variable("region", RuleType.String)
        .Variable("amount", RuleType.Decimal)
        .Variable("start", RuleType.Date)
        .Variable("flag", RuleType.Bool, nullable: true)
        .Build();

    private static readonly RuleEnvironment Env = RuleEnvironment.Create(Schema);

    private static RuleInputs In(long? age = 30, string region = "ATTICA", decimal amount = 100m, string start = "2026-11-01", bool? flag = null)
    {
        var b = Schema.NewInputs().Set("region", region).Set("amount", amount)
            .Set("start", DateOnly.ParseExact(start, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        if (age is { } a)
        {
            b.Set("age", a);
        }

        if (flag is { } f)
        {
            b.Set("flag", f);
        }

        return b.Build();
    }

    private static DecisionTableDefinition Table(HitPolicy policy, params DecisionRule[] rules) => new(
        new DecisionTableMetadata("T-1", "1.0", DecisionTableStatus.Active, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)),
        policy,
        new[] { new InputColumn("driverAge", RuleType.Int.Nullable(), "age"), new InputColumn("area", RuleType.String, "region") },
        new[] { new OutputColumn("label", RuleType.String), new OutputColumn("factor", RuleType.Decimal) },
        rules);

    private static DecisionRule R(string id, string age, string region, string label = "\"x\"", string factor = "1", int priority = 0) =>
        new(id, new[] { age, region }, new[] { label, factor }) { Priority = priority };

    private static readonly DateOnly AsOf = new(2026, 11, 1);

    [Theory]
    [InlineData("-", "30", true)]
    [InlineData("", "30", true)]
    [InlineData("< 30", "29", true)]
    [InlineData("< 30", "30", false)]
    [InlineData("<= 30", "30", true)]
    [InlineData("> 30", "31", true)]
    [InlineData(">= 30", "29", false)]
    [InlineData("== 30", "30", true)]
    [InlineData("!= 30", "31", true)]
    [InlineData("30", "30", true)]
    [InlineData("(10 + 20)", "30", true)]
    [InlineData("[18..25]", "25", true)]
    [InlineData("[18..25)", "25", false)]
    [InlineData("(18..25]", "18", false)]
    [InlineData("(18..25)", "19", true)]
    [InlineData("in [1, 2, 30]", "30", true)]
    [InlineData("in[1, 2]", "30", false)]
    [InlineData("not in [1, 2]", "30", true)]
    [InlineData("not in[30]", "30", false)]
    [InlineData("? age > 18 && region == \"ATTICA\"", "30", true)]
    [InlineData("null", "null", true)]
    [InlineData("null", "30", false)]
    [InlineData("not null", "30", true)]
    [InlineData("not null", "null", false)]
    [InlineData("< 30", "null", false)]
    [InlineData("[18..25]", "null", false)]
    [InlineData("!= 30", "null", true)]
    [InlineData("in [1, 2]", "null", false)]
    [InlineData("-", "null", true)]
    public void Condition_cells_cover_comparisons_ranges_sets_any_and_no_value(string cell, string age, bool matches)
    {
        var table = CompiledDecisionTable.Compile(Table(HitPolicy.First, R("R1", cell, "-")), Env);
        long? a = age == "null" ? null : long.Parse(age, System.Globalization.CultureInfo.InvariantCulture);
        table.Evaluate(In(a), AsOf).MatchedRuleIds.Count.ShouldBe(matches ? 1 : 0);
    }

    [Theory]
    [InlineData("[\"a..b\"..\"z\"]", "region != null && region >= (\"a..b\") && region <= (\"z\")")]
    [InlineData("[date(\"2026-01-01\")..date(\"2027-01-01\"))", "region != null && region >= (date(\"2026-01-01\")) && region < (date(\"2027-01-01\"))")]
    [InlineData("[[1, 2]..[3]]", "region != null && region >= ([1, 2]) && region <= ([3])")]
    [InlineData("['it\\'s'..'z']", "region != null && region >= ('it\\'s') && region <= ('z')")]
    [InlineData("[1, 2]", "region == ([1, 2])")]
    [InlineData("(1)", "region == ((1))")]
    [InlineData("  -  ", null)]
    [InlineData("\"ATTICA\"", "region == (\"ATTICA\")")]
    [InlineData("[\"unterminated..]", "region == ([\"unterminated..])")]
    public void Condition_cell_translation(string cell, string? expected) => ConditionCell.Translate(cell, "region").ShouldBe(expected);

    [Fact]
    public void Date_range_cells_work()
    {
        var def = new DecisionTableDefinition(
            new DecisionTableMetadata("T-D", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.First,
            new[] { new InputColumn("startDate", RuleType.Date, "start") },
            new[] { new OutputColumn("season", RuleType.String) },
            new[]
            {
                new DecisionRule("WINTER", new[] { "[date(\"2026-12-01\")..date(\"2027-03-01\"))" }, new[] { "\"winter\"" }),
                new DecisionRule("OTHER", new[] { "-" }, new[] { "\"other\"" }),
            });
        var table = CompiledDecisionTable.Compile(def, Env);
        table.Evaluate(In(start: "2027-02-28"), AsOf).Match!.RuleId.ShouldBe("WINTER");
        table.Evaluate(In(start: "2027-03-01"), AsOf).Match!.RuleId.ShouldBe("OTHER");
    }

    [Fact]
    public void First_stops_at_the_first_match_and_records_cell_outcomes()
    {
        var table = CompiledDecisionTable.Compile(
            Table(HitPolicy.First, R("R1", "< 18", "-"), R("R2", ">= 18", "\"CRETE\""), R("R3", ">= 18", "-", "\"adult\"", "1.5"), R("R4", "-", "-")),
            Env);
        var result = table.Evaluate(In(30), AsOf);
        result.IsSuccess.ShouldBeTrue();
        result.MatchedRuleIds.ShouldBe(new[] { "R3" });
        result.Match!.Output("label").ShouldBe(new StringValue("adult"));
        result.Match.Output("factor").ShouldBe(new DecimalValue(1.5m));
        result.Match.RowIndex.ShouldBe(2);
        Should.Throw<ArgumentException>(() => result.Match.Output("nope"));

        var trace = result.Trace;
        trace.Rules.Select(r => r.RuleId).ShouldBe(new[] { "R1", "R2", "R3" });
        trace.Rules[0].Cells.ShouldBe(new[] { CellOutcome.NotMatched, CellOutcome.NotEvaluated });
        trace.Rules[1].Cells.ShouldBe(new[] { CellOutcome.Matched, CellOutcome.NotMatched });
        trace.Rules[2].Cells.ShouldBe(new[] { CellOutcome.Matched, CellOutcome.Any });
        trace.Rules[2].Matched.ShouldBeTrue();
        trace.Inputs.Select(i => i.ToString()).ShouldBe(new[] { "driverAge = 30", "area = \"ATTICA\"" });
        trace.TableId.ShouldBe("T-1");
        trace.Version.ShouldBe("1.0");
        trace.ContentHash.ShouldBe(table.ContentHash);
        trace.HitPolicy.ShouldBe(HitPolicy.First);
        trace.MatchedRuleIds.ShouldBe(new[] { "R3" });
        trace.Matches.Single().RuleId.ShouldBe("R3");
        trace.StepsUsed.ShouldBeGreaterThan(0);
        trace.Expressions.ShouldBeEmpty();
    }

    [Fact]
    public void Unique_rejects_overlapping_matches()
    {
        var table = CompiledDecisionTable.Compile(Table(HitPolicy.Unique, R("R1", ">= 18", "-"), R("R2", "< 65", "-"), R("R3", "< 18", "-")), Env);
        var result = table.Evaluate(In(30), AsOf);
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(RuleErrorCode.HitPolicyViolation);
        result.Error.CodeText.ShouldBe("PLT-ERR-HIT-POLICY-VIOLATION");
        result.Error.Message.ShouldContain("R1, R2");
        result.Matches.ShouldBeEmpty();
        table.Evaluate(In(70), AsOf).MatchedRuleIds.ShouldBe(new[] { "R1" });
        table.Evaluate(In(10), AsOf).Error!.Message.ShouldContain("R2, R3");
    }

    [Fact]
    public void Priority_picks_the_highest_priority_and_the_earlier_row_on_ties()
    {
        var table = CompiledDecisionTable.Compile(
            Table(HitPolicy.Priority, R("LOW", "-", "-", priority: 1), R("HIGH-A", ">= 18", "-", priority: 9), R("HIGH-B", ">= 18", "-", priority: 9)),
            Env);
        table.Evaluate(In(30), AsOf).MatchedRuleIds.ShouldBe(new[] { "HIGH-A" });
        table.Evaluate(In(10), AsOf).MatchedRuleIds.ShouldBe(new[] { "LOW" });
        table.Evaluate(In(30), AsOf).Trace.Rules.Count.ShouldBe(3);
    }

    [Fact]
    public void Collect_returns_all_matches_in_row_order_and_empty_when_none()
    {
        var table = CompiledDecisionTable.Compile(
            Table(HitPolicy.Collect, R("A", ">= 18", "-", "\"a\""), R("B", "< 18", "-", "\"b\""), R("C", "-", "\"ATTICA\"", "\"c\"")),
            Env);
        var result = table.Evaluate(In(30), AsOf);
        result.MatchedRuleIds.ShouldBe(new[] { "A", "C" });
        result.Matches.Select(m => m.Output("label").ToString()).ShouldBe(new[] { "\"a\"", "\"c\"" });
        table.Evaluate(In(10, "CRETE"), AsOf).MatchedRuleIds.ShouldBe(new[] { "B" });
        var none = CompiledDecisionTable.Compile(Table(HitPolicy.Collect, R("A", ">= 99", "-")), Env).Evaluate(In(30), AsOf);
        none.IsSuccess.ShouldBeTrue();
        none.Matches.ShouldBeEmpty();
        none.Match.ShouldBeNull();
    }

    [Fact]
    public void Only_active_and_effective_versions_evaluate_in_production()
    {
        var def = Table(HitPolicy.First, R("R1", "-", "-"));
        foreach (var status in new[] { DecisionTableStatus.Draft, DecisionTableStatus.Submitted, DecisionTableStatus.Approved, DecisionTableStatus.Superseded, DecisionTableStatus.Retired })
        {
            var table = CompiledDecisionTable.Compile(def with { Metadata = def.Metadata with { Status = status } }, Env);
            var result = table.Evaluate(In(), AsOf);
            result.Error!.Code.ShouldBe(RuleErrorCode.TableNotActive);
            result.Error.CodeText.ShouldBe("PLT-ERR-TABLE-NOT-ACTIVE");
            table.EvaluateForTesting(In()).IsSuccess.ShouldBeTrue();
        }

        var active = CompiledDecisionTable.Compile(def, Env);
        active.Evaluate(In(), new DateOnly(2025, 12, 31)).Error!.Code.ShouldBe(RuleErrorCode.TableNotActive);
        active.Evaluate(In(), new DateOnly(2026, 1, 1)).IsSuccess.ShouldBeTrue();
        active.Evaluate(In(), new DateOnly(2026, 12, 31)).IsSuccess.ShouldBeTrue();
        active.Evaluate(In(), new DateOnly(2027, 1, 1)).Error!.Message.ShouldContain("not effective on 2027-01-01");
    }

    [Fact]
    public void Content_hash_covers_content_but_not_metadata()
    {
        var def = Table(HitPolicy.First, R("R1", "< 18", "-"), R("R2", "-", "-"));
        var baseline = CompiledDecisionTable.Compile(def, Env);
        baseline.ContentHash.Length.ShouldBe(64);
        CompiledDecisionTable.Compile(def with { Metadata = def.Metadata with { Version = "9.9", Status = DecisionTableStatus.Draft, Description = "x" } }, Env)
            .ContentHash.ShouldBe(baseline.ContentHash);
        CompiledDecisionTable.Compile(Table(HitPolicy.First, R("R1", "<   18", "-"), R("R2", "", "-")), Env).ContentHash.ShouldBe(baseline.ContentHash);
        CompiledDecisionTable.Compile(Table(HitPolicy.First, R("R1", "< 19", "-"), R("R2", "-", "-")), Env).ContentHash.ShouldNotBe(baseline.ContentHash);
        CompiledDecisionTable.Compile(Table(HitPolicy.Collect, R("R1", "< 18", "-"), R("R2", "-", "-")), Env).ContentHash.ShouldNotBe(baseline.ContentHash);
        CompiledDecisionTable.Compile(Table(HitPolicy.First, R("R1", "< 18", "-", priority: 3), R("R2", "-", "-")), Env).ContentHash.ShouldNotBe(baseline.ContentHash);
        CompiledDecisionTable.Compile(Table(HitPolicy.First, R("R1", "< 18", "-", factor: "2"), R("R2", "-", "-")), Env).ContentHash.ShouldNotBe(baseline.ContentHash);
        baseline.Metadata.ShouldBe(def.Metadata);
        baseline.Definition.ShouldBe(def);
        baseline.InputSchema.ShouldBeSameAs(Schema);
    }

    [Fact]
    public void Same_version_and_inputs_give_identical_results_1000_times()
    {
        var table = MotorFixtures.UwTable;
        var inputs = MotorFixtures.Inputs(new DateOnly(2006, 3, 10), 20000m, 2, 1, 110, "PRIVATE");
        var first = table.Evaluate(inputs, MotorFixtures.AsOf, new DecisionEvaluationOptions { DetailedTrace = true });
        string Fingerprint(DecisionResult r) => string.Join("|", r.MatchedRuleIds) + "#" + string.Join("|", r.Matches.SelectMany(m => m.Outputs)) + "#"
            + string.Join("|", r.Trace.Rules.Select(x => x.RuleId + ":" + string.Join(",", x.Cells))) + "#"
            + string.Join("|", r.Trace.Expressions.SelectMany(e => e.Entries.Select(t => e.Label + t))) + "#" + r.Trace.StepsUsed;
        string expected = Fingerprint(first);
        for (int i = 0; i < 1000; i++)
        {
            Fingerprint(table.Evaluate(inputs, MotorFixtures.AsOf, new DecisionEvaluationOptions { DetailedTrace = true })).ShouldBe(expected);
        }
    }

    [Fact]
    public void Detailed_trace_includes_sub_expression_values()
    {
        var result = MotorFixtures.UwTable.Evaluate(
            MotorFixtures.Inputs(new DateOnly(2009, 1, 15), 10000m, 0, 0), MotorFixtures.AsOf, new DecisionEvaluationOptions { DetailedTrace = true });
        result.MatchedRuleIds.ShouldBe(new[] { "UW-AGE-U18" });
        var variable = result.Trace.Expressions.First(e => e.Label == "variable 'mainDriverAge'");
        variable.Source.ShouldStartWith("ageAt(");
        variable.Entries[^1].Value.ShouldBe(IntValue.Of(17));
        result.Trace.Expressions.ShouldContain(e => e.Label == "rule 'UW-AGE-U18' condition 'driverAge'");
        result.Trace.Expressions.ShouldContain(e => e.Label == "rule 'UW-AGE-U18' output 'issueKey'");
    }

    [Fact]
    public void Variables_are_computed_in_order_and_may_use_earlier_variables()
    {
        var def = Table(HitPolicy.First, R("R1", "-", "-", "\"v\"", "doubled")) with
        {
            Variables = new[]
            {
                new TableVariable("base", RuleType.Decimal, "amount * 1.5"),
                new TableVariable("doubled", RuleType.Decimal, "base * 2"),
                new TableVariable("names", RuleType.ListOf(RuleType.Int), "[1, 2, 3].map(i, i * 2)"),
            },
        };
        var result = CompiledDecisionTable.Compile(def, Env).Evaluate(In(amount: 10m), AsOf);
        result.Match!.Output("factor").ToString().ShouldBe("30.0");
        result.Trace.Variables.Select(v => v.ToString()).ShouldBe(new[] { "base = 15.0", "doubled = 30.0", "names = [2, 4, 6]" });

        var forward = Table(HitPolicy.First, R("R1", "-", "-")) with
        {
            Variables = new[] { new TableVariable("first", RuleType.Int, "second + 1"), new TableVariable("second", RuleType.Int, "1") },
        };
        Should.Throw<RuleCompileException>(() => CompiledDecisionTable.Compile(forward, Env)).Errors.Single().Context.ShouldBe("variable 'first'");
    }

    [Fact]
    public void Compile_reports_every_error_with_its_cell_context()
    {
        var def = Table(HitPolicy.First, R("R1", "< unknownInput", "-"), R("R2", "-", "\"A\"", "42", "\"not a decimal\""), R("R3", "\"text\"", "-"));
        var ex = Should.Throw<RuleCompileException>(() => CompiledDecisionTable.Compile(def, Env));
        ex.Errors.Select(e => (e.Code, e.Context ?? string.Empty)).ShouldBe(new[]
        {
            (RuleErrorCode.UnknownIdentifier, "rule 'R1' condition 'driverAge' (< unknownInput)"),
            (RuleErrorCode.ResultTypeMismatch, "rule 'R2' output 'label'"),
            (RuleErrorCode.ResultTypeMismatch, "rule 'R2' output 'factor'"),
            (RuleErrorCode.NoMatchingOverload, "rule 'R3' condition 'driverAge' (\"text\")"),
        });
        ex.Errors[0].Message.ShouldContain("unknownInput");
        ex.Errors[0].ToString().ShouldContain("[rule 'R1' condition 'driverAge' (< unknownInput)]");
    }

    [Fact]
    public void Structural_errors_are_reported()
    {
        var meta = new DecisionTableMetadata("T", "1", DecisionTableStatus.Draft, new DateOnly(2026, 1, 1));
        var cols = new[] { new InputColumn("c", RuleType.Int, "1") };
        var outs = new[] { new OutputColumn("o", RuleType.Int) };

        string[] Errors(DecisionTableDefinition d) =>
            Should.Throw<RuleCompileException>(() => CompiledDecisionTable.Compile(d, Env)).Errors.Select(e => e.Message).ToArray();

        Errors(new DecisionTableDefinition(meta, HitPolicy.First, cols, Array.Empty<OutputColumn>(), Array.Empty<DecisionRule>()))
            .ShouldContain("a decision table needs at least one output column");
        Errors(new DecisionTableDefinition(meta, HitPolicy.First, cols, outs, new[] { new DecisionRule("R", new[] { "-", "-" }, new[] { "1" }) }))
            .ShouldContain(m => m.Contains("exactly 1 condition cell", StringComparison.Ordinal));
        Errors(new DecisionTableDefinition(meta, HitPolicy.First, cols, outs, new[] { new DecisionRule("R", new[] { "-" }, Array.Empty<string>()) }))
            .ShouldContain(m => m.Contains("exactly 1 output expression", StringComparison.Ordinal));
        Errors(new DecisionTableDefinition(meta, HitPolicy.First, cols, outs, new[] { new DecisionRule("R", new[] { "-" }, new[] { "1" }), new DecisionRule("R", new[] { "-" }, new[] { "1" }) }))
            .ShouldContain(m => m.Contains("duplicated", StringComparison.Ordinal));
        Errors(new DecisionTableDefinition(meta, HitPolicy.First, new[] { new InputColumn("age", RuleType.Int, "1") }, outs, Array.Empty<DecisionRule>()))
            .ShouldContain(m => m.Contains("declared more than once", StringComparison.Ordinal));
        Errors(new DecisionTableDefinition(meta, HitPolicy.First, new[] { new InputColumn("bad name", RuleType.Int, "1") }, outs, Array.Empty<DecisionRule>()))
            .ShouldContain(m => m.Contains("not a valid", StringComparison.Ordinal));
        Errors(new DecisionTableDefinition(meta, HitPolicy.First, cols, new[] { new OutputColumn("o", RuleType.Int), new OutputColumn("o", RuleType.Int) }, Array.Empty<DecisionRule>()))
            .ShouldContain(m => m.Contains("invalid or duplicated", StringComparison.Ordinal));
        Errors(new DecisionTableDefinition(meta with { TableId = " " }, HitPolicy.First, cols, outs, Array.Empty<DecisionRule>()))
            .ShouldContain("table id and version are required");
        Errors(new DecisionTableDefinition(meta with { EffectiveTo = new DateOnly(2026, 1, 1) }, HitPolicy.First, cols, outs, Array.Empty<DecisionRule>()))
            .ShouldContain(m => m.Contains("effective-to", StringComparison.Ordinal));
        Errors(new DecisionTableDefinition(meta, HitPolicy.First, null!, outs, Array.Empty<DecisionRule>()))
            .ShouldContain(m => m.Contains("must not be null", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluation_errors_fail_closed_with_context()
    {
        var def = Table(HitPolicy.First, R("R1", "-", "-", "\"x\"", "amount / (age - 30)"));
        var result = CompiledDecisionTable.Compile(def, Env).Evaluate(In(30), AsOf);
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(RuleErrorCode.DivisionByZero);
        result.Error.Context.ShouldBe("rule 'R1' output 'factor'");
        result.Matches.ShouldBeEmpty();

        var cellError = Table(HitPolicy.First, R("R1", "? 1 / (age - 30) > 0", "-"));
        var r2 = CompiledDecisionTable.Compile(cellError, Env).Evaluate(In(30), AsOf);
        r2.Error!.Context.ShouldBe("rule 'R1' condition 'driverAge'");
        r2.Trace.Rules.Single().Matched.ShouldBeFalse();

        var nullCell = Table(HitPolicy.First, R("R1", "? flag", "-"));
        CompiledDecisionTable.Compile(nullCell, Env).Evaluate(In(), AsOf).Error!.Code.ShouldBe(RuleErrorCode.NullValue);

        var columnError = new DecisionTableDefinition(
            new DecisionTableMetadata("T-C", "1", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
            HitPolicy.First,
            new[] { new InputColumn("ratio", RuleType.Decimal, "amount / 0") },
            new[] { new OutputColumn("o", RuleType.Int) },
            new[] { new DecisionRule("R1", new[] { "-" }, new[] { "1" }) });
        CompiledDecisionTable.Compile(columnError, Env).Evaluate(In(), AsOf).Error!.Context.ShouldBe("input column 'ratio'");

        var variableError = Table(HitPolicy.First, R("R1", "-", "-")) with { Variables = new[] { new TableVariable("v", RuleType.Int, "[1][5]") } };
        CompiledDecisionTable.Compile(variableError, Env).Evaluate(In(), AsOf).Error!.Code.ShouldBe(RuleErrorCode.IndexOutOfRange);

        Should.Throw<ArgumentException>(() => CompiledDecisionTable.Compile(def, Env).Evaluate(T.Default, AsOf));
    }

    [Fact]
    public void Activation_requires_passing_test_cases()
    {
        var table = MotorFixtures.UwTable;
        var good = new[]
        {
            new DecisionTableTestCase("young refer", MotorFixtures.Inputs(new DateOnly(2006, 3, 10), 1m, 0, 0, 110), new[] { "UW-YOUNG-POWER" })
            {
                ExpectedOutputs = new[] { new[] { new NamedValue("lane", "SENIOR") } },
            },
            new DecisionTableTestCase("adult accept", MotorFixtures.Inputs(new DateOnly(1980, 1, 1), 1m, 0, 0), new[] { "UW-ACCEPT" }),
        };
        var ready = table.CheckActivationReadiness(good);
        ready.CanActivate.ShouldBeTrue();
        ready.RefusalReason.ShouldBeNull();
        ready.Outcomes.ShouldAllBe(o => o.Passed);
        ready.UncoveredRuleIds.ShouldBe(new[] { "UW-AGE-U18", "UW-USAGE", "UW-AGE-80" });

        var strict = table.CheckActivationReadiness(good, requireEveryRuleCovered: true);
        strict.CanActivate.ShouldBeFalse();
        strict.RefusalReason.ShouldBe("rule(s) without a test case: UW-AGE-U18, UW-USAGE, UW-AGE-80");

        var failing = good.Append(new DecisionTableTestCase("wrong expectation", MotorFixtures.Inputs(new DateOnly(1980, 1, 1), 1m, 0, 0), new[] { "UW-AGE-80" })).ToArray();
        var refused = table.CheckActivationReadiness(failing);
        refused.CanActivate.ShouldBeFalse();
        refused.RefusalReason.ShouldBe("failing test case(s): wrong expectation");
        refused.FailingCases.Single().Failure.ShouldBe("expected rules [UW-AGE-80] but matched [UW-ACCEPT]");

        table.CheckActivationReadiness(Array.Empty<DecisionTableTestCase>()).RefusalReason.ShouldBe("a table version must ship with test cases");
    }

    [Fact]
    public void Test_cases_compare_outputs_and_expected_errors()
    {
        var table = MotorFixtures.UwTable;
        var inputs = MotorFixtures.Inputs(new DateOnly(1980, 1, 1), 1m, 0, 0);
        var outcomes = table.RunTests(new[]
        {
            new DecisionTableTestCase("bad output", inputs, new[] { "UW-ACCEPT" }) { ExpectedOutputs = new[] { new[] { new NamedValue("lane", "MANUAL") } } },
            new DecisionTableTestCase("unknown output", inputs, new[] { "UW-ACCEPT" }) { ExpectedOutputs = new[] { new[] { new NamedValue("nope", "x") } } },
            new DecisionTableTestCase("count", inputs, new[] { "UW-ACCEPT" }) { ExpectedOutputs = Array.Empty<IReadOnlyList<NamedValue>>() },
            new DecisionTableTestCase("expects error", inputs, Array.Empty<string>()) { ExpectedError = RuleErrorCode.DivisionByZero },
        });
        outcomes.Select(o => o.Failure).ShouldBe(new[]
        {
            "match UW-ACCEPT: expected lane = \"MANUAL\" but got \"AUTO\"",
            "match UW-ACCEPT: expected nope = \"x\" but got (no such output)",
            "expected outputs for a different number of matches",
            "expected error RULE-DIVISION-BY-ZERO but got success",
        });

        var erroring = CompiledDecisionTable.Compile(Table(HitPolicy.Unique, R("A", "-", "-"), R("B", "-", "-")) with
        {
            Metadata = new DecisionTableMetadata("T", "1", DecisionTableStatus.Draft, new DateOnly(2026, 1, 1)),
        }, Env);
        var errorOutcomes = erroring.RunTests(new[]
        {
            new DecisionTableTestCase("expects violation", In(), Array.Empty<string>()) { ExpectedError = RuleErrorCode.HitPolicyViolation },
            new DecisionTableTestCase("unexpected violation", In(), new[] { "A" }),
        });
        errorOutcomes[0].Passed.ShouldBeTrue();
        errorOutcomes[1].Failure!.ShouldStartWith("evaluation failed: PLT-ERR-HIT-POLICY-VIOLATION");
    }
}
