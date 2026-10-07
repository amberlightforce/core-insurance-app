using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreIns.Rules.Tests;

/// <summary>Shared schemas, environment and helpers.</summary>
internal static class T
{
    public static readonly ObjectSchema Driver = ObjectSchema.Define("Driver")
        .Field("name", RuleType.String)
        .Field("birthDate", RuleType.Date)
        .Field("licenceDate", RuleType.Date)
        .Field("claimsLast3Years", RuleType.Int)
        .Field("isMain", RuleType.Bool)
        .Build();

    public static readonly ObjectSchema Vehicle = ObjectSchema.Define("Vehicle")
        .Field("value", RuleType.Decimal)
        .Field("powerKw", RuleType.Int)
        .Field("usage", RuleType.String)
        .Field("trackerFitted", RuleType.Bool, nullable: true)
        .Build();

    public static readonly ObjectSchema Policy = ObjectSchema.Define("Policy")
        .Field("effectiveDate", RuleType.Date)
        .Field("productCode", RuleType.String)
        .Field("territory", RuleType.String)
        .Build();

    public static readonly InputSchema Schema = InputSchema.Define()
        .Variable("x", RuleType.Int)
        .Variable("y", RuleType.Int)
        .Variable("d", RuleType.Decimal)
        .Variable("e", RuleType.Decimal)
        .Variable("s", RuleType.String)
        .Variable("b", RuleType.Bool)
        .Variable("n", RuleType.Decimal, nullable: true)
        .Variable("dt", RuleType.Date)
        .Variable("ts", RuleType.Timestamp)
        .Variable("dur", RuleType.Duration)
        .Variable("ints", RuleType.ListOf(RuleType.Int))
        .Variable("decs", RuleType.ListOf(RuleType.Decimal))
        .Variable("tags", RuleType.ListOf(RuleType.String))
        .Variable("names", RuleType.MapOf(RuleType.String, RuleType.Int))
        .Variable("vehicle", RuleType.ObjectOf(Vehicle))
        .Variable("policy", RuleType.ObjectOf(Policy))
        .Variable("drivers", RuleType.ListOf(RuleType.ObjectOf(Driver)))
        .Variable("premium", RuleType.Decimal)
        .Variable("owner", RuleType.ObjectOf(Driver), nullable: true)
        .Variable("extras", RuleType.MapOf(RuleType.String, RuleType.Int), nullable: true)
        .Variable("ns", RuleType.String, nullable: true)
        .Variable("nd", RuleType.Date, nullable: true)
        .Variable("ni", RuleType.Int, nullable: true)
        .Variable("nl", RuleType.ListOf(RuleType.Int), nullable: true)
        .Build();

    public static readonly HostFunction MktRound = new(
        "mkt.round",
        new[] { RuleType.Decimal, RuleType.String },
        RuleType.Decimal,
        args => DecimalRounding.Round(((DecimalValue)args[0]).Value, 2, ((StringValue)args[1]).Value == "tax.document" ? RoundingMode.HalfEven : RoundingMode.HalfUp));

    public static readonly HostFunction Failing = new("failing", new[] { RuleType.Int }, RuleType.Int, _ => throw new InvalidOperationException("boom"));

    public static readonly HostFunction WrongType = new("wrongType", Array.Empty<RuleType>(), RuleType.Int, _ => new StringValue("not an int"));

    public static readonly HostFunction Twice = new("twice", new[] { RuleType.Decimal }, RuleType.Decimal, args => ((DecimalValue)args[0]).Value * 2m);

    /// <summary>
    /// Limits for functional tests: production defaults except a generous regex timeout and wall-clock deadline, so
    /// results never depend on machine load. Timeout behaviour itself is tested separately with explicit limits.
    /// </summary>
    public static readonly RuleLimits Functional = new()
    {
        RegexTimeout = TimeSpan.FromTicks(10 * TimeSpan.TicksPerSecond),
        MaxEvaluationTime = TimeSpan.FromTicks(60 * TimeSpan.TicksPerSecond),
    };

    public static readonly RuleEnvironment Env = RuleEnvironment.Create(Schema, Functional, new[] { MktRound, Failing, WrongType, Twice });

    public static ObjectValue MakeDriver(string name, string birth, int claims = 0, bool isMain = true, string licence = "2015-01-01") =>
        Driver.NewValue()
            .Set("name", name)
            .Set("birthDate", DateOnly.Parse(birth, System.Globalization.CultureInfo.InvariantCulture))
            .Set("licenceDate", DateOnly.Parse(licence, System.Globalization.CultureInfo.InvariantCulture))
            .Set("claimsLast3Years", claims)
            .Set("isMain", isMain)
            .Build();

    public static RuleInputs.Builder Inputs() => Schema.NewInputs()
        .Set("x", 7)
        .Set("y", 2)
        .Set("d", 10.50m)
        .Set("e", 3m)
        .Set("s", "Motor Policy")
        .Set("b", true)
        .Set("dt", new DateOnly(2026, 11, 1))
        .Set("ts", new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero))
        .Set("dur", new TimeSpan(1, 0, 0))
        .Set("ints", RuleValue.List(1, 2, 3))
        .Set("decs", RuleValue.List(1.5m, 2.25m))
        .Set("tags", RuleValue.List("young", "urban"))
        .Set("names", RuleValue.Map(new[]
        {
            new KeyValuePair<RuleValue, RuleValue>("a", 1),
            new KeyValuePair<RuleValue, RuleValue>("b", 2),
        }))
        .Set("vehicle", Vehicle.NewValue().Set("value", 12000m).Set("powerKw", 85).Set("usage", "PRIVATE").Build())
        .Set("policy", Policy.NewValue().Set("effectiveDate", new DateOnly(2026, 11, 1)).Set("productCode", "MOTOR").Set("territory", "ATTICA").Build())
        .Set("drivers", RuleValue.List(MakeDriver("Maria", "1990-06-15"), MakeDriver("Nikos", "2005-03-10", claims: 1, isMain: false)))
        .Set("premium", 100.10m);

    public static readonly RuleInputs Default = Inputs().Build();

    /// <summary>Warms up the regex engine and the evaluator JIT once per test run.</summary>
    public static readonly bool WarmedUp = Env.Compile("s.matches('^Motor') && size(ints.map(i, i + 1)) > 0").Evaluate(Default).IsSuccess;

    public static EvaluationResult Run(string source, RuleInputs? inputs = null, EvaluationOptions? options = null) =>
        Env.Compile(source).Evaluate(inputs ?? Default, options);

    /// <summary>Evaluates and returns the invariant display text of the value.</summary>
    public static string Eval(string source) => Run(source).GetValueOrThrow().ToString();

    public static RuleEvaluationError EvalError(string source)
    {
        var r = Run(source);
        return r.Error ?? throw new InvalidOperationException($"expected an evaluation error for {source} but got {r.Value}");
    }

    public static RuleCompileError CompileError(string source, RuleType? expected = null, RuleEnvironment? env = null)
    {
        var r = (env ?? Env).TryCompile(source, expected);
        return r.Errors.Count == 1 ? r.Errors[0] : throw new InvalidOperationException($"expected one compile error for {source}");
    }

    public static IEnumerable<string> Lines(IEnumerable<object> items) => items.Select(i => i.ToString()!);
}
