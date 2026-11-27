using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Domain;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Underwriting.Services;

/// <summary>
/// The read-only authoring operations of rule sets (BO-UW-01): <c>validate</c> and <c>test</c> (the full report), <c>simulate</c>
/// (a version against stored evaluations, nothing written) and <c>export</c>.
/// </summary>
internal sealed class RuleSetAuthoringQueries(RuleSetAuthoringStore store, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public const int DefaultSimulateLimit = 100;

    // The stored facts keep the youngest driver's age band, not the birth date (P2): the simulation rebuilds an age from the band.
    private static readonly Dictionary<string, int> BandAge = new(StringComparer.Ordinal)
    {
        ["UNDER_18"] = 17, ["FROM_18_TO_20"] = 18, ["FROM_21_TO_24"] = 21, ["FROM_25_TO_29"] = 25, ["FROM_30_TO_69"] = 30, ["FROM_70"] = 70,
    };

    public async Task<Result<RuleSetValidationReport>> ValidateAsync(RuleSetRef request, CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(request.RuleSetCode, request.Version, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(request.RuleSetCode, request.Version);
        }

        return RuleSetAuthoring.ToReport(record.RuleSetCode, record.VersionNo, RuleSetAnalysis.Analyse(RuleSetJson.Deserialize(record.Definition)));
    }

    public async Task<Result<RuleSetExportResponse>> ExportAsync(RuleSetRef request, CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(request.RuleSetCode, request.Version, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(request.RuleSetCode, request.Version);
        }

        var dto = RuleSetJson.Deserialize(record.Definition);
        return new RuleSetExportResponse
        {
            Document = new RuleSetExportDocument
            {
                Format = RuleSetAuthoring.ExportFormat,
                DefinitionHash = RuleSetAuthoring.DefinitionHash(dto),
                Definition = RuleSetAuthoring.ToContract(dto),
            },
        };
    }

    public async Task<Result<RuleSetSimulateResponse>> SimulateAsync(RuleSetSimulateRequest request, CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(request.RuleSetCode, request.Version, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(request.RuleSetCode, request.Version);
        }

        var dto = RuleSetJson.Deserialize(record.Definition);
        CoreIns.Rules.DecisionTables.CompiledDecisionTable table;
        try
        {
            table = CompiledRuleSet.BuildTable(dto);
        }
        catch (Exception ex) when (ex is CoreIns.Rules.RuleCompileException or ArgumentException or FormatException or DomainException)
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "The version does not compile, so it cannot be simulated: " + ex.Message);
        }

        var ruleTypes = dto.Rules.ToDictionary(r => r.Id, r => RuleType(dto, r), StringComparer.Ordinal);
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var limit = Math.Clamp(request.Limit ?? DefaultSimulateLimit, 1, 1000);
        var evaluations = await store.RecentEvaluationsAsync(legalEntity, record.ProductCode, record.Checkpoint, limit, cancellationToken).ConfigureAwait(false);

        var items = new List<RuleSetSimulateItem>();
        int newReferrals = 0, newDeclines = 0;
        foreach (var evaluation in evaluations)
        {
            var recorded = RecordedRules(evaluation.Trace);
            var recordedOutcome = evaluation.Outcome;
            try
            {
                var facts = JsonSerializer.Deserialize<UwFactsRecord>(evaluation.Facts, RuleSetJson.Options)!;
                var effective = DateOnly.ParseExact(facts.EffectiveDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var birth = facts.YoungestDriverAgeBand is { } band && BandAge.TryGetValue(band, out var age) ? new[] { effective.AddYears(-age) } : [];
                var risk = new UwRisk(
                    "vehicle-1", new DateOnly(facts.FirstRegistrationYear, 1, 1),
                    facts.VehicleValue is null ? 0m : decimal.Parse(facts.VehicleValue, CultureInfo.InvariantCulture), facts.EngineCc, facts.Usage, birth, facts.ClaimsLast5Years);
                var result = table.EvaluateForTesting(CompiledRuleSet.Facts(risk, effective));
                if (!result.IsSuccess)
                {
                    items.Add(Item(evaluation.EvaluationId, recorded, [], recordedOutcome, recordedOutcome, result.Error!.ToString()));
                    continue;
                }

                var simulated = result.MatchedRuleIds;
                var simulatedOutcome = Outcome(simulated.Select(id => ruleTypes.GetValueOrDefault(id)));
                if (!recorded.SequenceEqual(simulated, StringComparer.Ordinal) || simulatedOutcome != recordedOutcome)
                {
                    items.Add(Item(evaluation.EvaluationId, recorded, simulated, recordedOutcome, simulatedOutcome, null));
                    if (simulatedOutcome != recordedOutcome)
                    {
                        newReferrals += simulatedOutcome == "REFER" ? 1 : 0;
                        newDeclines += simulatedOutcome == "DECLINE" ? 1 : 0;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            {
                items.Add(Item(evaluation.EvaluationId, recorded, [], recordedOutcome, recordedOutcome, "The stored facts cannot be read: " + ex.Message));
            }
        }

        // TODO(hardening): store the exact youngest driver age with the facts so the simulation needs no age-band approximation.
        return new RuleSetSimulateResponse
        {
            RuleSetCode = record.RuleSetCode,
            Version = record.VersionNo,
            Evaluated = evaluations.Count,
            Changed = items.Count,
            NewReferrals = newReferrals,
            NewDeclines = newDeclines,
            Items = items,
            Warnings = [.. evaluations.Count == 0 ? new[] { "UW-WARN-NO-EVALUATIONS" } : ["UW-WARN-DRIVER-AGE-FROM-BAND"]],
        };
    }

    private static RuleSetSimulateItem Item(Guid id, IReadOnlyList<string> recorded, IReadOnlyList<string> simulated, string recordedOutcome, string simulatedOutcome, string? error) => new()
    {
        EvaluationId = id.ToString("D"),
        RecordedRuleIds = recorded,
        SimulatedRuleIds = simulated,
        AddedRuleIds = [.. simulated.Except(recorded, StringComparer.Ordinal)],
        RemovedRuleIds = [.. recorded.Except(simulated, StringComparer.Ordinal)],
        RecordedOutcome = recordedOutcome,
        SimulatedOutcome = simulatedOutcome,
        Error = error,
    };

    private static string Outcome(IEnumerable<string?> ruleTypes)
    {
        var types = ruleTypes.ToList();
        return types.Contains("DECLINE") ? "DECLINE" : types.Contains("REFER") ? "REFER" : "ACCEPT";
    }

    /// <summary>The rule type (DECLINE or REFER) a rule outputs: the literal in its ruleType cell.</summary>
    private static string? RuleType(RuleSetDto dto, RuleRowDto rule)
    {
        var index = dto.Outputs.FindIndex(o => o.Name == "ruleType");
        return index >= 0 && index < rule.Outputs.Count ? rule.Outputs[index].Trim().Trim('"') : null;
    }

    private static IReadOnlyList<string> RecordedRules(string trace)
    {
        try
        {
            using var document = JsonDocument.Parse(trace);
            return document.RootElement.TryGetProperty("matchedRules", out var rules) && rules.ValueKind == JsonValueKind.Array
                ? [.. rules.EnumerateArray().Select(r => r.GetString() ?? string.Empty)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DomainError NotFound(string code, string version) => DomainError.Of(ModuleCode.UW, "RULESET-NOT-FOUND", $"Rule set {code} {version} does not exist.");
}
