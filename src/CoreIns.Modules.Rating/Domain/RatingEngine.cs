using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Errors;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Rating.Domain;

/// <summary>One step of the explainable trace of a coverage (REQ-RAT-104): what was looked up, what changed and why.</summary>
internal sealed record StepTrace(
    string StepId,
    string Kind,
    string? Table,
    string? TableHash,
    string? RuleId,
    string? Value,
    string Before,
    string After,
    bool Applied,
    string ExplanationEn,
    string ExplanationEl);

/// <summary>The premium of one coverage with its trace.</summary>
internal sealed record CoveragePremium(string Coverage, decimal Premium, IReadOnlyList<StepTrace> Steps);

/// <summary>A tax or levy rate read from MKT configuration (or looked up by class), with its provenance.</summary>
internal sealed record ConfiguredRate(string Key, decimal Rate, string SourceLayer, Guid ValueVersionId);

/// <summary>
/// The pure rating function (REQ-RAT-045..047): the same artefact, input and effective date always give the same
/// premium and the same trace. No clock, no randomness, no I/O. All arithmetic runs in the rule engine, which refuses
/// silent precision loss (RULE-PRECISION-LOSS becomes RAT-ERR-SCALE); money is rounded only by the artefact's declared
/// ROUND step and the tax plan's declared rounding.
/// </summary>
internal static class RatingEngine
{
    public static CoveragePremium RateCoverage(CompiledArtefact artefact, MotorRisk risk, string coverage, DateOnly effectiveDate)
    {
        var facts = RatingFacts.For(risk, coverage, effectiveDate);
        var running = 0m;
        var started = false;
        var trace = new List<StepTrace>();
        foreach (var step in artefact.Definition.Steps)
        {
            var before = started ? Format(running) : "-";
            string? ruleId = null;
            string? value = null;
            string basis = string.Empty;
            string? tableHash = null;
            var applied = true;
            var lookup = 0m;
            if (step.Table is { } tableCode)
            {
                var compiled = artefact.Tables[tableCode];
                tableHash = compiled.Hash;
                var result = compiled.Table.Evaluate(facts, effectiveDate);
                if (!result.IsSuccess)
                {
                    throw Map(result.Error!, $"{tableCode} for {coverage}");
                }

                if (result.Match is null)
                {
                    if (step.Kind == "BASE")
                    {
                        throw new DomainException(DomainError.Of(ModuleCode.RAT, "DOMAIN", $"No base rate for coverage {coverage} (table {tableCode})."));
                    }

                    applied = false;
                }
                else
                {
                    ruleId = result.Match.RuleId;
                    lookup = ((DecimalValue)result.Match.Output("value")).Value;
                    value = Format(lookup);
                    basis = step.Kind == "BASE" ? ((StringValue)result.Match.Output("basis")).Value : string.Empty;
                }
            }

            if (applied)
            {
                var inputs = RatingFacts.StepSchema.NewInputs()
                    .Set("coverage", coverage)
                    .Set("vehicleValue", risk.VehicleValue)
                    .Set("running", running)
                    .Set("value", lookup)
                    .Set("basis", basis)
                    .Build();
                var evaluation = artefact.Expression(step).Evaluate(inputs);
                if (!evaluation.IsSuccess)
                {
                    throw Map(evaluation.Error!, step.Id);
                }

                running = ((DecimalValue)evaluation.Value!).Value;
                started = true;
            }

            trace.Add(new StepTrace(
                step.Id, step.Kind, step.Table, tableHash, ruleId, value, before, started ? Format(running) : "-", applied,
                step.ExplainEn, step.ExplainEl));
        }

        return new CoveragePremium(coverage, running, trace);
    }

    /// <summary>The tax or levy of one coverage line: base x configured rate, rounded as the plan declares (REQ-RAT-109, -110).</summary>
    public static Money TaxAmount(Money premium, decimal rate, TaxPlanDto plan)
    {
        MidpointRounding mode;
        switch (plan.Mode)
        {
            case "HalfUp":
                mode = MidpointRounding.AwayFromZero;
                break;
            case "HalfEven":
                mode = MidpointRounding.ToEven;
                break;
            default:
                throw new DomainException(DomainError.Of(ModuleCode.RAT, "TAX", $"Unknown rounding mode {plan.Mode}."));
        }

        try
        {
            return premium.Multiply(rate).Round(plan.Places, mode);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidOperationException or ArgumentException)
        {
            throw new DomainException(DomainError.Of(ModuleCode.RAT, "SCALE", $"The {plan.ChargeType} amount cannot be represented exactly: {ex.Message}"));
        }
    }

    public static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a rate from a configuration JSON value: a number or string, or an object keyed by tax class.</summary>
    public static decimal ReadRate(JsonElement value, string taxClass, string key)
    {
        var element = value;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty(taxClass, out element))
            {
                throw new DomainException(DomainError.Of(ModuleCode.RAT, "TAX", $"Configuration {key} has no rate for tax class {taxClass}."));
            }
        }

        decimal rate;
        var ok = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out rate),
            JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rate),
            _ => (rate = -1m) > 0m,
        };
        if (!ok || rate < 0m || rate > 1m)
        {
            throw new DomainException(DomainError.Of(ModuleCode.RAT, "TAX", $"Configuration {key} does not hold a rate between 0 and 1."));
        }

        return rate;
    }

    private static DomainException Map(RuleEvaluationError error, string where) => error.Code switch
    {
        RuleErrorCode.PrecisionLoss => new(DomainError.Of(ModuleCode.RAT, "SCALE", $"{where}: {error}")),
        RuleErrorCode.HitPolicyViolation => new(DomainError.Of(ModuleCode.RAT, "HIT-POLICY", $"{where}: {error}")),
        RuleErrorCode.Timeout or RuleErrorCode.Cancelled => new(DomainError.Of(ModuleCode.RAT, "DATA-UNAVAILABLE", $"{where}: {error}")),
        _ => new(DomainError.Of(ModuleCode.RAT, "DOMAIN", $"{where}: {error}")),
    };

    /// <summary>The trace as JSON for the worksheet.</summary>
    public static JsonArray TraceJson(IReadOnlyList<StepTrace> steps) => new(steps.Select(s => (JsonNode)new JsonObject
    {
        ["step"] = s.StepId,
        ["kind"] = s.Kind,
        ["table"] = s.Table,
        ["tableHash"] = s.TableHash,
        ["rule"] = s.RuleId,
        ["value"] = s.Value,
        ["before"] = s.Before,
        ["after"] = s.After,
        ["applied"] = s.Applied,
        ["explanation"] = new JsonObject { ["en"] = s.ExplanationEn, ["el"] = s.ExplanationEl },
    }).ToArray());
}
