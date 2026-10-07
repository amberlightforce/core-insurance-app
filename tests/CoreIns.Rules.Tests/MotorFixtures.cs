using System;
using System.Collections.Generic;
using System.Linq;
using CoreIns.Rules.DecisionTables;

namespace CoreIns.Rules.Tests;

/// <summary>Realistic (synthetic) Greek motor underwriting and rating rules used by the golden tests.</summary>
internal static class MotorFixtures
{
    public static readonly DateOnly AsOf = new(2026, 11, 1);

    public static readonly ObjectSchema Driver = ObjectSchema.Define("Driver")
        .Field("birthDate", RuleType.Date)
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
        .Field("territory", RuleType.String)
        .Build();

    public static readonly InputSchema Schema = InputSchema.Define()
        .Variable("policy", RuleType.ObjectOf(Policy))
        .Variable("vehicle", RuleType.ObjectOf(Vehicle))
        .Variable("drivers", RuleType.ListOf(RuleType.ObjectOf(Driver)))
        .Variable("ncdYears", RuleType.Int)
        .Build();

    public static readonly RuleEnvironment Env = RuleEnvironment.Create(Schema);

    private static readonly TableVariable MainDriverAge =
        new("mainDriverAge", RuleType.Int, "ageAt(drivers.filter(d, d.isMain)[0].birthDate, policy.effectiveDate)");

    private static DecisionTableMetadata Meta(string id) => new(id, "1.0", DecisionTableStatus.Active, new DateOnly(2026, 1, 1));

    private static DecisionRule Rule(string id, string[] conditions, params string[] outputs) => new(id, conditions, outputs);

    /// <summary>Driver age bands → age factor (UNIQUE).</summary>
    public static readonly DecisionTableDefinition AgeDefinition = new(
        Meta("RAT-MOTOR-GR-AGE"),
        HitPolicy.Unique,
        new[] { new InputColumn("driverAge", RuleType.Int, "mainDriverAge") },
        new[] { new OutputColumn("ageFactor", RuleType.Decimal) },
        new[]
        {
            Rule("AGE-18-24", new[] { "[18..25)" }, "1.80"),
            Rule("AGE-25-29", new[] { "[25..30)" }, "1.30"),
            Rule("AGE-30-64", new[] { "[30..65)" }, "1.00"),
            Rule("AGE-65-74", new[] { "[65..75)" }, "1.15"),
            Rule("AGE-75P", new[] { ">= 75" }, "1.40"),
        })
    {
        Variables = new[] { MainDriverAge },
    };

    /// <summary>Vehicle value bands → value factor (UNIQUE).</summary>
    public static readonly DecisionTableDefinition ValueDefinition = new(
        Meta("RAT-MOTOR-GR-VALUE"),
        HitPolicy.Unique,
        new[] { new InputColumn("vehicleValue", RuleType.Decimal, "vehicle.value") },
        new[] { new OutputColumn("valueFactor", RuleType.Decimal) },
        new[]
        {
            Rule("VAL-A", new[] { "< 5000" }, "0.90"),
            Rule("VAL-B", new[] { "[5000..15000)" }, "1.00"),
            Rule("VAL-C", new[] { "[15000..30000)" }, "1.15"),
            Rule("VAL-D", new[] { "[30000..60000)" }, "1.35"),
            Rule("VAL-E", new[] { ">= 60000" }, "1.60"),
        });

    /// <summary>No-claims discount with claims step-back (PRIORITY).</summary>
    public static readonly DecisionTableDefinition NcdDefinition = new(
        Meta("RAT-MOTOR-GR-NCD"),
        HitPolicy.Priority,
        new[]
        {
            new InputColumn("ncd", RuleType.Int, "ncdYears"),
            new InputColumn("claims", RuleType.Int, "sum(drivers.map(d, d.claimsLast3Years))"),
        },
        new[] { new OutputColumn("ncdDiscount", RuleType.Decimal) },
        new[]
        {
            Rule("NCD-CLAIMS2", new[] { "-", ">= 2" }, "0.00") with { Priority = 100 },
            Rule("NCD-CLAIMS1", new[] { ">= 3", "1" }, "0.15") with { Priority = 50 },
            Rule("NCD-0", new[] { "0", "-" }, "0.00") with { Priority = 10 },
            Rule("NCD-1", new[] { "[1..3)", "-" }, "0.10") with { Priority = 10 },
            Rule("NCD-3", new[] { "[3..5)", "-" }, "0.25") with { Priority = 10 },
            Rule("NCD-5", new[] { ">= 5", "-" }, "0.40") with { Priority = 10 },
        });

    /// <summary>Underwriting referral rules (FIRST).</summary>
    public static readonly DecisionTableDefinition UwDefinition = new(
        Meta("UW-MOTOR-GR-B"),
        HitPolicy.First,
        new[]
        {
            new InputColumn("driverAge", RuleType.Int, "mainDriverAge"),
            new InputColumn("powerKw", RuleType.Int, "vehicle.powerKw"),
            new InputColumn("usage", RuleType.String, "vehicle.usage"),
        },
        new[]
        {
            new OutputColumn("decision", RuleType.String),
            new OutputColumn("lane", RuleType.String),
            new OutputColumn("issueKey", RuleType.String),
        },
        new[]
        {
            Rule("UW-AGE-U18", new[] { "< 18", "-", "-" }, "\"DECLINE\"", "\"KNOCKOUT\"", "\"UW.AGE.UNDER18\""),
            Rule("UW-YOUNG-POWER", new[] { "[18..21)", "> 100", "-" }, "\"REFER\"", "\"SENIOR\"", "\"UW.YOUNG.HIGHPOWER\""),
            Rule("UW-USAGE", new[] { "-", "-", "in [\"TAXI\", \"RIDESHARE\"]" }, "\"REFER\"", "\"STANDARD\"", "\"UW.USAGE.\" + usage"),
            Rule("UW-AGE-80", new[] { ">= 80", "-", "-" }, "\"REFER\"", "\"STANDARD\"", "\"UW.AGE.OVER80\""),
            Rule("UW-ACCEPT", new[] { "-", "-", "-" }, "\"ACCEPT\"", "\"AUTO\"", "\"UW.NONE\""),
        })
    {
        Variables = new[] { MainDriverAge },
    };

    /// <summary>Applicable surcharges and discounts (COLLECT).</summary>
    public static readonly DecisionTableDefinition SurchargeDefinition = new(
        Meta("RAT-MOTOR-GR-SURCHARGE"),
        HitPolicy.Collect,
        new[]
        {
            new InputColumn("youngestAge", RuleType.Int, "min(drivers.map(d, ageAt(d.birthDate, policy.effectiveDate)))"),
            new InputColumn("powerKw", RuleType.Int, "vehicle.powerKw"),
            new InputColumn("territory", RuleType.String, "policy.territory"),
            new InputColumn("tracker", RuleType.Bool.Nullable(), "vehicle.trackerFitted"),
        },
        new[] { new OutputColumn("code", RuleType.String), new OutputColumn("loading", RuleType.Decimal) },
        new[]
        {
            Rule("SUR-YOUNG", new[] { "< 25", "-", "-", "-" }, "\"YOUNG_DRIVER\"", "0.20"),
            Rule("SUR-POWER", new[] { "-", "> 120", "-", "-" }, "\"HIGH_POWER\"", "0.10"),
            Rule("SUR-URBAN", new[] { "-", "-", "in [\"ATTICA\", \"THESSALONIKI\"]", "-" }, "\"URBAN\"", "0.05"),
            Rule("SUR-NOTRACKER", new[] { "-", "-", "-", "null" }, "\"NO_TRACKER_INFO\"", "0"),
            Rule("DISC-TRACKER", new[] { "-", "-", "-", "true" }, "\"TRACKER\"", "-0.05"),
        });

    public static readonly CompiledDecisionTable AgeTable = CompiledDecisionTable.Compile(AgeDefinition, Env);
    public static readonly CompiledDecisionTable ValueTable = CompiledDecisionTable.Compile(ValueDefinition, Env);
    public static readonly CompiledDecisionTable NcdTable = CompiledDecisionTable.Compile(NcdDefinition, Env);
    public static readonly CompiledDecisionTable UwTable = CompiledDecisionTable.Compile(UwDefinition, Env);
    public static readonly CompiledDecisionTable SurchargeTable = CompiledDecisionTable.Compile(SurchargeDefinition, Env);

    public static readonly InputSchema PremiumSchema = InputSchema.Define()
        .Variable("baseRate", RuleType.Decimal)
        .Variable("ageFactor", RuleType.Decimal)
        .Variable("valueFactor", RuleType.Decimal)
        .Variable("ncdDiscount", RuleType.Decimal)
        .Variable("powerKw", RuleType.Int)
        .Build();

    /// <summary>Premium with explicit rounding point (HalfUp, 2 dp) and a minimum premium.</summary>
    public static readonly CompiledExpression Premium = RuleEnvironment.Create(PremiumSchema).Compile(
        "max(round(baseRate * ageFactor * valueFactor * (1.0 - ncdDiscount) * (powerKw > 100 ? 1.10 : 1.00), 2, \"HalfUp\"), 150.00)",
        RuleType.Decimal);

    public static ObjectValue MakeDriver(DateOnly birth, int claims = 0, bool isMain = true) =>
        Driver.NewValue().Set("birthDate", birth).Set("claimsLast3Years", claims).Set("isMain", isMain).Build();

    public static RuleInputs Inputs(
        DateOnly mainBirth,
        decimal vehicleValue,
        int ncdYears,
        int claims,
        int powerKw = 85,
        string usage = "PRIVATE",
        string territory = "CRETE",
        bool? tracker = null,
        IEnumerable<ObjectValue>? otherDrivers = null)
    {
        var vehicle = Vehicle.NewValue().Set("value", vehicleValue).Set("powerKw", powerKw).Set("usage", usage);
        if (tracker is { } t)
        {
            vehicle.Set("trackerFitted", t);
        }

        var drivers = new List<RuleValue> { MakeDriver(mainBirth, claims) };
        drivers.AddRange(otherDrivers ?? Enumerable.Empty<ObjectValue>());
        return Schema.NewInputs()
            .Set("policy", Policy.NewValue().Set("effectiveDate", AsOf).Set("territory", territory).Build())
            .Set("vehicle", vehicle.Build())
            .Set("drivers", RuleValue.List(drivers))
            .Set("ncdYears", ncdYears)
            .Build();
    }

    /// <summary>Rates a case through the three rating tables and the premium expression.</summary>
    public static string Rate(RuleInputs inputs, decimal baseRate, int powerKw)
    {
        decimal Factor(CompiledDecisionTable table, string output)
        {
            var result = table.Evaluate(inputs, AsOf);
            if (!result.IsSuccess || result.Match is null)
            {
                throw new InvalidOperationException($"{table.Metadata.TableId}: {result.Error?.ToString() ?? "no match"}");
            }

            return ((DecimalValue)result.Match.Output(output)).Value;
        }

        var premiumInputs = PremiumSchema.NewInputs()
            .Set("baseRate", baseRate)
            .Set("ageFactor", Factor(AgeTable, "ageFactor"))
            .Set("valueFactor", Factor(ValueTable, "valueFactor"))
            .Set("ncdDiscount", Factor(NcdTable, "ncdDiscount"))
            .Set("powerKw", powerKw)
            .Build();
        return Premium.EvaluateOrThrow(premiumInputs).ToString();
    }
}
