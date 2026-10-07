using System;
using System.Linq;
using CoreIns.Rules.DecisionTables;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

/// <summary>The Given/When/Then acceptance criteria of the PRD requirements this package implements.</summary>
public class RequirementTests
{
    [Fact]
    public void REQ_PLT_364_premium_rounding_point_is_deterministic()
    {
        // Given premium * 0.15 with premium 100.10 and a half-up rounding to 2 dp at the declared rounding point
        var schema = InputSchema.Define().Variable("premium", RuleType.Decimal).Build();
        var inputs = schema.NewInputs().Set("premium", 100.10m).Build();
        for (int run = 0; run < 100; run++)
        {
            // When evaluated (fresh environment = another node)
            var value = RuleEnvironment.Create(schema).Compile("round(premium * 0.15, 2, \"HalfUp\")", RuleType.Decimal).EvaluateOrThrow(inputs);

            // Then the result is 15.02 on every run and node
            value.ShouldBe(new DecimalValue(15.02m));
            value.ToString().ShouldBe("15.02");
        }
    }

    [Fact]
    public void REQ_PLT_364_undeclared_input_fails_activation_naming_the_input()
    {
        var schema = InputSchema.Define().Variable("premium", RuleType.Decimal).Build();
        var error = RuleEnvironment.Create(schema).TryCompile("premium * commissionRate").Errors.Single();
        error.Code.ShouldBe(RuleErrorCode.UnknownIdentifier);
        error.Message.ShouldContain("commissionRate");
    }

    [Fact]
    public void REQ_PLT_364_function_outside_the_adopted_subset_fails() =>
        T.CompileError("double(premium)").Code.ShouldBe(RuleErrorCode.UnsupportedFeature);

    [Fact]
    public void REQ_PFC_065_undefined_function_shows_position_and_clock_is_rejected()
    {
        var undefined = T.CompileError("x > 1 && isEligible(vehicle)");
        undefined.Code.ShouldBe(RuleErrorCode.UnknownFunction);
        undefined.Position.ShouldBe(new SourcePosition(9, 1, 10));

        // The PFC lint code PFC-LINT-NONDETERMINISTIC maps from RULE-NONDETERMINISTIC.
        T.CompileError("policy.effectiveDate < today()").Code.ShouldBe(RuleErrorCode.NonDeterministic);
        T.CompileError("ts < now()").CodeText.ShouldBe("RULE-NONDETERMINISTIC");
    }

    [Fact]
    public void REQ_RAT_076_shared_expression_type_checks_and_evaluates_identically_everywhere()
    {
        var rat = RuleEnvironment.Create(T.Schema).Compile("vehicle.powerKw > 100", RuleType.Bool);
        var plt = RuleEnvironment.Create(T.Schema).Compile("vehicle.powerKw > 100", RuleType.Bool);
        rat.ContentHash.ShouldBe(plt.ContentHash);
        rat.EvaluateOrThrow(T.Default).ShouldBe(plt.EvaluateOrThrow(T.Default));

        // Procedural constructs are rejected by the parser.
        T.CompileError("for (d in drivers) { x }").Code.ShouldBe(RuleErrorCode.ReservedWord);
        T.CompileError("while").Code.ShouldBe(RuleErrorCode.ReservedWord);
        T.CompileError("x = x + 1").Code.ShouldBe(RuleErrorCode.Syntax);
    }

    [Fact]
    public void REQ_RAT_089_conditional_step_records_the_evaluated_condition()
    {
        var schema = InputSchema.Define().Variable("coverages", RuleType.ListOf(RuleType.String)).Build();
        var condition = RuleEnvironment.Create(schema).Compile("\"THEFT\" in coverages", RuleType.Bool);
        var result = condition.Evaluate(schema.NewInputs().Set("coverages", RuleValue.List("MTPL", "FIRE")).Build(), new EvaluationOptions { Trace = true });
        result.Value.ShouldBe(BoolValue.False);
        result.Trace.Last().ToString().ShouldBe("\"THEFT\" in coverages = false");
    }

    [Fact]
    public void REQ_UW_032_collect_returns_both_matching_rows_in_order_with_ids()
    {
        var young = MotorFixtures.MakeDriver(new DateOnly(2004, 1, 1), isMain: false);
        var result = MotorFixtures.SurchargeTable.Evaluate(
            MotorFixtures.Inputs(new DateOnly(1980, 1, 1), 1m, 0, 0, powerKw: 150, otherDrivers: new[] { young }, tracker: false),
            MotorFixtures.AsOf);
        result.Matches.Select(m => (m.RuleId, m.RowIndex)).ShouldBe(new[] { ("SUR-YOUNG", 0), ("SUR-POWER", 1) });
    }

    [Fact]
    public void REQ_UW_033_rule_variable_driver_age()
    {
        var result = MotorFixtures.UwTable.Evaluate(MotorFixtures.Inputs(new DateOnly(2009, 1, 15), 1m, 0, 0), MotorFixtures.AsOf);
        result.Trace.Variables.Single().ShouldBe(new NamedValue("mainDriverAge", IntValue.Of(17)));
    }

    [Fact]
    public void REQ_UW_061_and_REQ_PLT_177_trace_has_table_version_hash_inputs_matched_rule_and_outputs()
    {
        var table = MotorFixtures.UwTable;
        var result = table.Evaluate(MotorFixtures.Inputs(new DateOnly(2009, 1, 15), 1m, 0, 0), MotorFixtures.AsOf);
        var trace = result.Trace;
        trace.TableId.ShouldBe("UW-MOTOR-GR-B");
        trace.Version.ShouldBe("1.0");
        trace.ContentHash.ShouldBe(table.ContentHash);
        trace.Inputs.ShouldContain(new NamedValue("driverAge", IntValue.Of(17)));
        trace.MatchedRuleIds.ShouldBe(new[] { "UW-AGE-U18" });
        trace.Rules.Single(r => r.Matched).RowIndex.ShouldBe(0);
        trace.Matches.Single().Output("issueKey").ShouldBe(new StringValue("UW.AGE.UNDER18"));
    }

    [Fact]
    public void REQ_PLT_175_unapproved_version_is_refused()
    {
        var draft = CompiledDecisionTable.Compile(
            MotorFixtures.UwDefinition with { Metadata = MotorFixtures.UwDefinition.Metadata with { Status = DecisionTableStatus.Approved } },
            MotorFixtures.Env);
        draft.Evaluate(MotorFixtures.Inputs(new DateOnly(1980, 1, 1), 1m, 0, 0), MotorFixtures.AsOf).Error!.CodeText.ShouldBe("PLT-ERR-TABLE-NOT-ACTIVE");
    }

    [Fact]
    public void REQ_PLT_176_failing_test_blocks_activation_and_is_shown()
    {
        var readiness = MotorFixtures.AgeTable.CheckActivationReadiness(new[]
        {
            new DecisionTableTestCase("36 years", MotorFixtures.Inputs(new DateOnly(1990, 6, 15), 1m, 0, 0), new[] { "AGE-30-64" }),
            new DecisionTableTestCase("21 years (wrong band)", MotorFixtures.Inputs(new DateOnly(2005, 3, 10), 1m, 0, 0), new[] { "AGE-25-29" }),
        });
        readiness.CanActivate.ShouldBeFalse();
        readiness.FailingCases.Single().Name.ShouldBe("21 years (wrong band)");
    }

    [Fact]
    public void REQ_FIN_051_unknown_event_field_fails_with_the_field_path()
    {
        var evt = ObjectSchema.Define("PremiumBooked").Field("grossAmount", RuleType.Decimal).Field("taxAmount", RuleType.Decimal).Build();
        var env = RuleEnvironment.Create(InputSchema.Define().Variable("event", RuleType.ObjectOf(evt)).Build());
        var error = env.TryCompile("event.grossAmount - event.commission").Errors.Single();
        error.Code.ShouldBe(RuleErrorCode.UnknownField);
        error.Message.ShouldContain("event.commission");
    }

    [Fact]
    public void REQ_BIL_286_amount_expression_uses_decimal_and_explicit_rounding()
    {
        var evt = ObjectSchema.Define("ChargeEvent").Field("amount", RuleType.Decimal).Field("installments", RuleType.Int).Build();
        var schema = InputSchema.Define().Variable("event", RuleType.ObjectOf(evt)).Build();
        var expr = RuleEnvironment.Create(schema).Compile("round(event.amount / event.installments, 2, \"HalfEven\")", RuleType.Decimal);
        var inputs = schema.NewInputs().Set("event", evt.NewValue().Set("amount", 1000.00m).Set("installments", 3).Build()).Build();
        expr.EvaluateOrThrow(inputs).ToString().ShouldBe("333.33");
    }

    [Theory]
    [InlineData("round(10.005, 2, \"HalfUp\")", "10.01")]
    [InlineData("round(0.125 + 0.125, 2, \"HalfEven\")", "0.25")]
    [InlineData("round(0.125, 2, \"HalfEven\") + round(0.125, 2, \"HalfEven\")", "0.24")]
    public void REQ_MKT_006_rounding_examples(string expression, string expected) => T.Eval(expression).ShouldBe(expected);

    [Fact]
    public void REQ_UW_057_evaluation_failure_is_typed_and_returns_no_decision()
    {
        var def = MotorFixtures.UwDefinition with
        {
            Variables = new[] { new TableVariable("mainDriverAge", RuleType.Int, "ageAt(drivers.filter(d, d.isMain)[0].birthDate, policy.effectiveDate)") },
        };
        var table = CompiledDecisionTable.Compile(def, MotorFixtures.Env);
        var noMainDriver = MotorFixtures.Schema.NewInputs()
            .Set("policy", MotorFixtures.Policy.NewValue().Set("effectiveDate", MotorFixtures.AsOf).Set("territory", "CRETE").Build())
            .Set("vehicle", MotorFixtures.Vehicle.NewValue().Set("value", 1m).Set("powerKw", 50).Set("usage", "PRIVATE").Build())
            .Set("drivers", RuleValue.List(MotorFixtures.MakeDriver(new DateOnly(1980, 1, 1), isMain: false)))
            .Set("ncdYears", 0)
            .Build();
        var result = table.Evaluate(noMainDriver, MotorFixtures.AsOf);
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(RuleErrorCode.IndexOutOfRange);
        result.Matches.ShouldBeEmpty();
    }
}
