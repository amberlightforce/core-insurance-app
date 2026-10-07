using System.Diagnostics;
using System.Globalization;
using CoreIns.Rules;
using CoreIns.Rules.DecisionTables;

// NFR-UW-001: uw.Rules.evaluate for one checkpoint on a motor job (<= 4 drivers, <= 2 vehicles, <= 150 rules),
// nested contexts evaluated in the rule-expression language: p95 <= 150 ms, p99 <= 300 ms.
const int Warmup = 200;
const int Runs = 2_000;
var p95Budget = TimeSpan.FromTicks(150 * TimeSpan.TicksPerMillisecond);
var p99Budget = TimeSpan.FromTicks(300 * TimeSpan.TicksPerMillisecond);

var driver = ObjectSchema.Define("Driver")
    .Field("birthDate", RuleType.Date)
    .Field("licenceDate", RuleType.Date)
    .Field("claimsLast3Years", RuleType.Int)
    .Field("convictions", RuleType.ListOf(RuleType.String))
    .Field("isMain", RuleType.Bool)
    .Build();
var vehicle = ObjectSchema.Define("Vehicle")
    .Field("value", RuleType.Decimal)
    .Field("powerKw", RuleType.Int)
    .Field("usage", RuleType.String)
    .Field("ageYears", RuleType.Int)
    .Build();
var schema = InputSchema.Define()
    .Variable("effectiveDate", RuleType.Date)
    .Variable("territory", RuleType.String)
    .Variable("drivers", RuleType.ListOf(RuleType.ObjectOf(driver)))
    .Variable("vehicles", RuleType.ListOf(RuleType.ObjectOf(vehicle)))
    .Build();
var env = RuleEnvironment.Create(schema);

var variables = new[]
{
    new TableVariable("ages", RuleType.ListOf(RuleType.Int), "drivers.map(d, ageAt(d.birthDate, effectiveDate))"),
    new TableVariable("youngest", RuleType.Int, "min(ages)"),
    new TableVariable("claims", RuleType.Int, "sum(drivers.map(d, d.claimsLast3Years))"),
    new TableVariable("maxValue", RuleType.Decimal, "max(vehicles.map(v, v.value))"),
};
var columns = new[]
{
    new InputColumn("youngestAge", RuleType.Int, "youngest"),
    new InputColumn("totalClaims", RuleType.Int, "claims"),
    new InputColumn("topValue", RuleType.Decimal, "maxValue"),
    new InputColumn("area", RuleType.String, "territory"),
};
var rules = Enumerable.Range(0, 150).Select(i => new DecisionRule(
    "UW-" + i.ToString("D3", CultureInfo.InvariantCulture),
    new[]
    {
        $"[{17 + (i % 60)}..{18 + (i % 60)})",
        i % 3 == 0 ? "-" : $">= {i % 4}",
        i % 5 == 0 ? $"> {1000 * i}" : "-",
        i % 7 == 0 ? $"? drivers.exists(d, d.convictions.exists(c, c.startsWith(\"DUI\"))) || vehicles.exists(v, v.powerKw > {100 + i})" : "-",
    },
    new[] { "\"REFER\"", $"\"UW.RULE.{i}\" + \".\" + area" })).ToArray();
var definition = new DecisionTableDefinition(
    new DecisionTableMetadata("UW-MOTOR-BENCH", "1.0", DecisionTableStatus.Active, new DateOnly(2026, 1, 1)),
    HitPolicy.Collect,
    columns,
    new[] { new OutputColumn("decision", RuleType.String), new OutputColumn("issueKey", RuleType.String) },
    rules) { Variables = variables };
var table = CompiledDecisionTable.Compile(definition, env);

ObjectValue Driver(int year, int claims, bool main, params string[] convictions) => driver.NewValue()
    .Set("birthDate", new DateOnly(year, 5, 17)).Set("licenceDate", new DateOnly(year + 18, 6, 1))
    .Set("claimsLast3Years", claims).Set("convictions", RuleValue.List(convictions.Select(c => (RuleValue)c)))
    .Set("isMain", main).Build();
ObjectValue Vehicle(decimal value, int kw) => vehicle.NewValue()
    .Set("value", value).Set("powerKw", kw).Set("usage", "PRIVATE").Set("ageYears", 4).Build();

var inputs = schema.NewInputs()
    .Set("effectiveDate", new DateOnly(2026, 11, 1))
    .Set("territory", "ATTICA")
    .Set("drivers", RuleValue.List(Driver(1980, 1, true), Driver(1983, 0, false, "SPEED-1"), Driver(2005, 0, false), Driver(2007, 2, false, "DUI-2")))
    .Set("vehicles", RuleValue.List(Vehicle(18_500m, 92), Vehicle(42_000m, 180)))
    .Build();

var asOf = new DateOnly(2026, 11, 1);
for (int i = 0; i < Warmup; i++)
{
    table.Evaluate(inputs, asOf);
}

var timings = new long[Runs];
DecisionResult? last = null;
for (int i = 0; i < Runs; i++)
{
    long start = Stopwatch.GetTimestamp();
    last = table.Evaluate(inputs, asOf);
    timings[i] = Stopwatch.GetElapsedTime(start).Ticks;
}

Array.Sort(timings);
static string Micros(long ticks) => (ticks / 10).ToString("N0", CultureInfo.InvariantCulture) + " µs";
var p50 = timings[Runs / 2];
var p95 = timings[(Runs * 95 / 100) - 1];
var p99 = timings[(Runs * 99 / 100) - 1];
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"NFR-UW-001 spike: 150 rules, 4 drivers, 2 vehicles, {Runs} runs; matches={last!.Matches.Count}, steps={last.Trace.StepsUsed}"));
Console.WriteLine($"p50 {Micros(p50)}  p95 {Micros(p95)}  p99 {Micros(p99)}  max {Micros(timings[^1])}");
bool ok = last.IsSuccess && p95 <= p95Budget.Ticks && p99 <= p99Budget.Ticks;
Console.WriteLine(ok ? "PASS (p95 <= 150 ms, p99 <= 300 ms)" : "FAIL");
return ok ? 0 : 1;
