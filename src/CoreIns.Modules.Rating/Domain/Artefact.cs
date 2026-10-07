using System.Globalization;
using System.Text.Json;
using CoreIns.Platform.Errors;
using CoreIns.Rules;
using CoreIns.Rules.DecisionTables;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Rating.Domain;

/// <summary>Data status of a rating table or artefact (D-SLC-04): the slice only has illustrative test data.</summary>
internal static class DataStatus
{
    public const string Illustrative = "ILLUSTRATIVE_TEST_DATA";
    public const string Approved = "APPROVED";
}

internal sealed record TableVariableDto(string Name, string Type, string Expression);

internal sealed record TableColumnDto(string Name, string Type, string Expression);

internal sealed record TableOutputDto(string Name, string Type);

internal sealed record TableRuleDto(string Id, List<string> Conditions, List<string> Outputs);

/// <summary>A rate table as stored: a decision table definition in plain JSON (REQ-RAT-081, -085).</summary>
internal sealed record TableDto(
    string Code,
    string Version,
    string HitPolicy,
    string DataStatus,
    string Description,
    string EffectiveFrom,
    List<TableVariableDto> Variables,
    List<TableColumnDto> Inputs,
    List<TableOutputDto> Outputs,
    List<TableRuleDto> Rules);

/// <summary>One step of the algorithm (REQ-RAT-077): base rate, multiplicative factor, minimum premium or explicit rounding.</summary>
internal sealed record StepDto(
    string Id,
    string Kind,
    string? Table,
    string NameEn,
    string NameEl,
    string ExplainEn,
    string ExplainEl,
    int? Places = null,
    string? Mode = null);

/// <summary>A tax or levy line the artefact asks for. The rate is read from MKT configuration under <see cref="RateKey"/> (REQ-RAT-109).</summary>
internal sealed record TaxPlanDto(
    string ChargeType,
    string Category,
    string RateKey,
    List<string>? Coverages,
    string DefaultClass,
    Dictionary<string, string> CoverageClasses,
    int Places,
    string Mode);

internal sealed record ArtefactMetadata(string DataStatus, bool NotATariff, string Note);

/// <summary>The rating artefact manifest (REQ-RAT-061): content-addressed, so the hash covers tables, steps and tax plan.</summary>
internal sealed record ArtefactDefinition(
    string Schema,
    string Code,
    string Label,
    string ProductCode,
    string ProductVersion,
    string EngineVersion,
    ArtefactMetadata Metadata,
    string Currency,
    bool AnnualTermsOnly,
    List<StepDto> Steps,
    Dictionary<string, string> Tables,
    List<TaxPlanDto> TaxPlan);

/// <summary>A compiled rate table with the SHA-256 content hash the artefact pins.</summary>
internal sealed record CompiledTable(TableDto Dto, string Hash, CompiledDecisionTable Table);

/// <summary>The rating engine version: the rule language plus this runtime, part of every worksheet hash.</summary>
internal static class EngineVersion
{
    public static readonly string Current = $"rat-engine/1.0 cel-subset/{RuleLanguage.Version}";
}

internal static class ArtefactJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public static Sha256Hash HashOf(ArtefactDefinition definition) => CanonicalJson.Hash(Serialize(definition));
}

/// <summary>An artefact with its tables compiled and its step expressions checked: immutable, thread-safe, cached by hash.</summary>
internal sealed class CompiledArtefact
{
    private readonly Dictionary<string, CompiledExpression> _stepExpressions = new(StringComparer.Ordinal);

    private CompiledArtefact(ArtefactDefinition definition, Sha256Hash hash, IReadOnlyDictionary<string, CompiledTable> tables)
    {
        Definition = definition;
        Hash = hash;
        Tables = tables;
    }

    public ArtefactDefinition Definition { get; }

    public Sha256Hash Hash { get; }

    public IReadOnlyDictionary<string, CompiledTable> Tables { get; }

    public CompiledExpression Expression(StepDto step) => _stepExpressions[step.Id];

    /// <summary>Compiles one table (types and cells are checked; all errors are reported together).</summary>
    public static CompiledTable CompileTable(TableDto dto, RuleEnvironment? environment = null)
    {
        environment ??= RuleEnvironment.Create(RatingFacts.Schema);
        try
        {
            var effective = DateOnly.ParseExact(dto.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var definition = new DecisionTableDefinition(
                new DecisionTableMetadata(dto.Code, dto.Version, DecisionTableStatus.Active, effective) { Description = dto.Description },
                Enum.Parse<HitPolicy>(dto.HitPolicy, ignoreCase: true),
                [.. dto.Inputs.Select(i => new InputColumn(i.Name, ParseType(i.Type), i.Expression))],
                [.. dto.Outputs.Select(o => new OutputColumn(o.Name, ParseType(o.Type)))],
                [.. dto.Rules.Select(r => new DecisionRule(r.Id, r.Conditions, r.Outputs))])
            {
                Variables = [.. dto.Variables.Select(v => new TableVariable(v.Name, ParseType(v.Type), v.Expression))],
            };
            var table = CompiledDecisionTable.Compile(definition, environment);
            return new CompiledTable(dto, table.ContentHash, table);
        }
        catch (RuleCompileException ex)
        {
            throw new DomainException(DomainError.Of(ModuleCode.RAT, "DOMAIN", $"Rate table {dto.Code} does not compile: {ex.Message}"));
        }
    }

    /// <summary>Compiles an artefact from its definition and its table versions (looked up by hash).</summary>
    public static CompiledArtefact Compile(ArtefactDefinition definition, Func<string, TableDto> tableByHash)
    {
        var hash = ArtefactJson.HashOf(definition);
        var environment = RuleEnvironment.Create(RatingFacts.Schema);
        var tables = new Dictionary<string, CompiledTable>(StringComparer.Ordinal);
        foreach (var (code, tableHash) in definition.Tables)
        {
            var compiled = CompileTable(tableByHash(tableHash), environment);
            if (!string.Equals(compiled.Hash, tableHash, StringComparison.Ordinal))
            {
                throw new DomainException(DomainError.Of(ModuleCode.RAT, "UNKNOWN-ARTEFACT", $"Table {code} does not match the hash the artefact pins."));
            }

            tables[code] = compiled;
        }

        var result = new CompiledArtefact(definition, hash, tables);
        var stepEnvironment = RuleEnvironment.Create(RatingFacts.StepSchema);
        foreach (var step in definition.Steps)
        {
            if (step.Table is { } table && !tables.ContainsKey(table))
            {
                throw new DomainException(DomainError.Of(ModuleCode.RAT, "DOMAIN", $"Step {step.Id} names table {table}, which the artefact does not pin."));
            }

            result._stepExpressions[step.Id] = stepEnvironment.Compile(ExpressionFor(step), RuleType.Decimal);
        }

        return result;
    }

    private static string ExpressionFor(StepDto step) => step.Kind switch
    {
        "BASE" => "basis == \"VEHICLE_VALUE\" ? vehicleValue * value : value",
        "FACTOR" => "running * value",
        "MIN" => "max(running, value)",
        "ROUND" when step.Places is >= 0 and <= 8 && step.Mode is "HalfUp" or "HalfEven" or "Up" or "Down" =>
            string.Create(CultureInfo.InvariantCulture, $"round(running, {step.Places}, \"{step.Mode}\")"),
        _ => throw new DomainException(DomainError.Of(ModuleCode.RAT, "DOMAIN", $"Step {step.Id} has an unsupported kind or rounding.")),
    };

    private static RuleType ParseType(string type) => type switch
    {
        "string" => RuleType.String,
        "int" => RuleType.Int,
        "decimal" => RuleType.Decimal,
        "date" => RuleType.Date,
        "bool" => RuleType.Bool,
        _ => throw new DomainException(DomainError.Of(ModuleCode.RAT, "DOMAIN", $"Unknown column type '{type}'.")),
    };
}
