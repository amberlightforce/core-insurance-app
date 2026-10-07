using System;
using System.Linq;
using CoreIns.Rules.DecisionTables;
using FsCheck.Xunit;

namespace CoreIns.Rules.Tests;

/// <summary>FsCheck properties: determinism, exact decimal arithmetic, short-circuit semantics, rounding invariants.</summary>
public class PropertyTests
{
    private static readonly InputSchema NumSchema = InputSchema.Define()
        .Variable("a", RuleType.Decimal)
        .Variable("b", RuleType.Decimal)
        .Variable("i", RuleType.Int)
        .Variable("j", RuleType.Int)
        .Variable("p", RuleType.Bool)
        .Variable("zero", RuleType.Int)
        .Build();

    private static readonly RuleEnvironment NumEnv = RuleEnvironment.Create(NumSchema);

    private static readonly CompiledExpression Add = NumEnv.Compile("a + b");
    private static readonly CompiledExpression Sub = NumEnv.Compile("a - b");
    private static readonly CompiledExpression Mul = NumEnv.Compile("a * b");
    private static readonly CompiledExpression Div = NumEnv.Compile("a / b");
    private static readonly CompiledExpression IntAdd = NumEnv.Compile("i + j");
    private static readonly CompiledExpression IntMul = NumEnv.Compile("i * j");
    private static readonly CompiledExpression IntDiv = NumEnv.Compile("i / j");
    private static readonly CompiledExpression IntMod = NumEnv.Compile("i % j");
    private static readonly CompiledExpression Less = NumEnv.Compile("a < b");
    private static readonly CompiledExpression Eq = NumEnv.Compile("a == b");
    private static readonly CompiledExpression Mixed = NumEnv.Compile("i + a");

    private static RuleInputs Inputs(decimal a = 0m, decimal b = 0m, long i = 0, long j = 0, bool p = false) =>
        NumSchema.NewInputs().Set("a", a).Set("b", b).Set("i", i).Set("j", j).Set("p", p).Set("zero", 0).Build();

    private static bool MatchesReference(CompiledExpression expr, RuleInputs inputs, Func<decimal> reference)
    {
        decimal expected;
        try
        {
            expected = reference();
        }
        catch (OverflowException)
        {
            return expr.Evaluate(inputs).Error?.Code == RuleErrorCode.Overflow;
        }
        catch (DivideByZeroException)
        {
            return expr.Evaluate(inputs).Error?.Code == RuleErrorCode.DivisionByZero;
        }

        var value = expr.Evaluate(inputs).Value as DecimalValue;
        // Exact: same value AND same scale (string form) as System.Decimal.
        return value is not null && value.Value == expected && value.ToString() == expected.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [Property(MaxTest = 500)]
    public bool Decimal_addition_is_exact(decimal a, decimal b) => MatchesReference(Add, Inputs(a, b), () => a + b);

    [Property(MaxTest = 500)]
    public bool Decimal_subtraction_is_exact(decimal a, decimal b) => MatchesReference(Sub, Inputs(a, b), () => a - b);

    [Property(MaxTest = 500)]
    public bool Decimal_multiplication_is_exact(decimal a, decimal b) => MatchesReference(Mul, Inputs(a, b), () => a * b);

    [Property(MaxTest = 500)]
    public bool Decimal_division_matches_System_Decimal(decimal a, decimal b) => MatchesReference(Div, Inputs(a, b), () => a / b);

    [Property(MaxTest = 300)]
    public bool Mixed_int_decimal_arithmetic_widens_exactly(long i, decimal a) => MatchesReference(Mixed, Inputs(a: a, i: i), () => i + a);

    [Property(MaxTest = 500)]
    public bool Int_arithmetic_is_checked(long i, long j)
    {
        var inputs = Inputs(i: i, j: j);
        bool ok = CheckInt(IntAdd, inputs, () => checked(i + j)) && CheckInt(IntMul, inputs, () => checked(i * j));
        if (j == 0)
        {
            return ok && IntDiv.Evaluate(inputs).Error?.Code == RuleErrorCode.DivisionByZero
                      && IntMod.Evaluate(inputs).Error?.Code == RuleErrorCode.DivisionByZero;
        }

        if (i == long.MinValue && j == -1)
        {
            return ok && IntDiv.Evaluate(inputs).Error?.Code == RuleErrorCode.Overflow && ((IntValue)IntMod.Evaluate(inputs).Value!).Value == 0;
        }

        return ok && CheckInt(IntDiv, inputs, () => i / j) && CheckInt(IntMod, inputs, () => i % j);
    }

    private static bool CheckInt(CompiledExpression expr, RuleInputs inputs, Func<long> reference)
    {
        long expected;
        try
        {
            expected = reference();
        }
        catch (OverflowException)
        {
            return expr.Evaluate(inputs).Error?.Code == RuleErrorCode.Overflow;
        }

        return expr.Evaluate(inputs).Value is IntValue v && v.Value == expected;
    }

    [Property(MaxTest = 300)]
    public bool Comparisons_agree_with_System_Decimal(decimal a, decimal b)
    {
        var inputs = Inputs(a, b);
        return ((BoolValue)Less.Evaluate(inputs).Value!).Value == (a < b)
            && ((BoolValue)Eq.Evaluate(inputs).Value!).Value == (a == b);
    }

    [Property(MaxTest = 200)]
    public bool Evaluation_is_deterministic(decimal a, decimal b, long i, bool p)
    {
        const string source = "p ? round(a * 1.15 + decimal(i % 1000), 2, \"HalfUp\") : (a > b ? a - b : b - a) / 3.0";
        var inputs = Inputs(a % 1_000_000m, b % 1_000_000m, i, 0, p);
        var first = NumEnv.Compile(source).Evaluate(inputs, new EvaluationOptions { Trace = true });
        var second = RuleEnvironment.Create(NumSchema).Compile(source).Evaluate(inputs, new EvaluationOptions { Trace = true });
        var third = NumEnv.GetOrCompile(source).Evaluate(inputs, new EvaluationOptions { Trace = true });
        return Same(first, second) && Same(first, third);
    }

    private static bool Same(EvaluationResult x, EvaluationResult y) =>
        Equals(x.Value?.ToString(), y.Value?.ToString())
        && Equals(x.Error?.Code, y.Error?.Code)
        && x.StepsUsed == y.StepsUsed
        && x.Trace.Select(t => t.ToString()).SequenceEqual(y.Trace.Select(t => t.ToString()));

    [Property(MaxTest = 100)]
    public bool And_short_circuits_on_false(bool p)
    {
        var result = NumEnv.Compile("p && 1 / zero == 1").Evaluate(Inputs(p: p), new EvaluationOptions { Trace = true });
        return p
            ? result.Error?.Code == RuleErrorCode.DivisionByZero
            : result.Value == BoolValue.False && result.Trace.All(t => !t.Expression.StartsWith("1 / zero", StringComparison.Ordinal) && t.Expression != "zero");
    }

    [Property(MaxTest = 100)]
    public bool Or_short_circuits_on_true(bool p)
    {
        var result = NumEnv.Compile("p || 1 / zero == 1").Evaluate(Inputs(p: p), new EvaluationOptions { Trace = true });
        return p
            ? result.Value == BoolValue.True && result.Trace.All(t => !t.Expression.StartsWith("1 / zero", StringComparison.Ordinal) && t.Expression != "zero")
            : result.Error?.Code == RuleErrorCode.DivisionByZero;
    }

    [Property(MaxTest = 100)]
    public bool Ternary_evaluates_only_the_chosen_branch(bool p, long i)
    {
        var result = NumEnv.Compile("p ? i : i / zero").Evaluate(Inputs(i: i, p: p));
        return p ? result.Value is IntValue v && v.Value == i : result.Error?.Code == RuleErrorCode.DivisionByZero;
    }

    [Property(MaxTest = 500)]
    public bool Rounding_respects_mode_invariants(decimal a, byte rawPlaces)
    {
        int places = rawPlaces % 11;
        decimal unit = places == 0 ? 1m : new decimal(1, 0, 0, false, (byte)places);
        var results = Enum.GetValues<RoundingMode>().ToDictionary(m => m, m => DecimalRounding.Round(a, places, m));
        bool withinUnit = results.Values.All(r => Math.Abs(r - a) < unit);
        bool directed = results[RoundingMode.Floor] <= a && results[RoundingMode.Ceiling] >= a
            && Math.Abs(results[RoundingMode.Down]) <= Math.Abs(a) && Math.Abs(results[RoundingMode.Up]) >= Math.Abs(a);
        bool halves = Math.Abs(results[RoundingMode.HalfUp] - a) <= unit / 2 && Math.Abs(results[RoundingMode.HalfEven] - a) <= unit / 2
            && Math.Abs(results[RoundingMode.HalfDown] - a) <= unit / 2;
        bool reference = results[RoundingMode.HalfEven] == decimal.Round(a, places, MidpointRounding.ToEven)
            && results[RoundingMode.HalfUp] == decimal.Round(a, places, MidpointRounding.AwayFromZero);
        bool scale = results.Values.All(r => r.Scale >= Math.Min(places, 28) || r == a);
        return withinUnit && directed && halves && reference && scale;
    }

    [Property(MaxTest = 200)]
    public bool Content_hash_ignores_whitespace(byte spaces, bool useComment)
    {
        string pad = new string(' ', spaces % 7) + (useComment ? "// note\n" : string.Empty);
        string source = "round(" + pad + "a" + pad + "*" + pad + "1.15" + pad + "," + pad + "2" + pad + ", \"HalfUp\")" + pad;
        return NumEnv.Compile(source).ContentHash == NumEnv.Compile("round(a * 1.15, 2, \"HalfUp\")").ContentHash;
    }

    [Property(MaxTest = 200)]
    public bool Decision_table_is_deterministic(byte age, short value)
    {
        var table = MotorFixtures.AgeTable;
        var inputs = MotorFixtures.Inputs(new DateOnly(2026, 11, 1).AddYears(-(age % 100)).AddDays(-1), Math.Abs((int)value) * 10m, 0, 0);
        var first = table.Evaluate(inputs, MotorFixtures.AsOf);
        var second = table.Evaluate(inputs, MotorFixtures.AsOf);
        return first.MatchedRuleIds.SequenceEqual(second.MatchedRuleIds)
            && first.Matches.SelectMany(m => m.Outputs).Select(o => o.ToString()).SequenceEqual(second.Matches.SelectMany(m => m.Outputs).Select(o => o.ToString()))
            && first.Trace.Rules.Select(r => string.Join(",", r.Cells)).SequenceEqual(second.Trace.Rules.Select(r => string.Join(",", r.Cells)));
    }
}
