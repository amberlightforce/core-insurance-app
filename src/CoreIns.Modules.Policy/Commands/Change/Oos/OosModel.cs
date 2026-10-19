using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Commands.Change.Oos;

/// <summary>One change of a term's effective history: when, the full premium rate set after it, the risk after it and the transaction it came from.</summary>
internal sealed record OosEntry(Instant EffectiveAt, IReadOnlyList<ChargeRate> Rates, RiskTree Tree, Guid Source);

/// <summary>One entry of the chain stored on an out-of-sequence transaction.</summary>
internal sealed record OosChainEntry(Instant EffectiveAt, IReadOnlyList<IntentRate> Rates, RiskTree RiskTree, Guid Source);

/// <summary>
/// The intent stored on an out-of-sequence transaction (<c>version</c> 2, kind <c>OOS</c>): the new change (as a version 1 intent has it)
/// and the complete corrected chain of changes in effective order. The replay of the term restarts from the opening at such a transaction
/// and applies the chain; later in-sequence changes then apply on top.
/// </summary>
internal sealed record OosIntentRecord(
    int Version, string Kind, Instant EffectiveAt, IReadOnlyList<IntentRate> NewRates, RiskTree RiskTree, Guid SetId, IReadOnlyList<OosChainEntry> Chain);

/// <summary>Reads and writes the intents the term replay and the out-of-sequence planner share.</summary>
internal static class OosIntents
{
    public const int Version = 2;

    public const string Kind = "OOS";

    public static string Write(Instant effectiveAt, IEnumerable<ChargeRate> rates, RiskTree tree, Guid setId, IEnumerable<OosEntry> chain) =>
        JobSupport.Json(new OosIntentRecord(
            Version, Kind, effectiveAt, Rates(rates), tree, setId,
            [.. chain.Select(e => new OosChainEntry(e.EffectiveAt, Rates(e.Rates), e.Tree, e.Source))]));

    /// <summary>The out-of-sequence intent, or null when the JSON is anything else.</summary>
    public static OosIntentRecord? TryRead(string intentJson)
    {
        using var document = JsonDocument.Parse(intentJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != Version || !root.TryGetProperty("kind", out var kind) || kind.GetString() != Kind)
        {
            return null;
        }

        return JobSupport.FromJson<OosIntentRecord>(intentJson);
    }

    public static IReadOnlyList<ChargeRate> ToRates(IEnumerable<IntentRate> rates) =>
        [.. rates.Select(r => new ChargeRate(r.Element, r.Coverage, r.ChargeType, r.Category, r.AnnualRate))];

    private static List<IntentRate> Rates(IEnumerable<ChargeRate> rates) =>
        [.. rates.OrderBy(r => r.Key).Select(r => new IntentRate(r.ElementLocator, r.CoverageCode, r.ChargeType, r.ChargeCategory, r.AnnualRate))];

    /// <summary>
    /// The effective history of a term's changes as the replay sees it: every in-sequence change in order, replaced wholesale by the chain
    /// of an out-of-sequence transaction when one is met. Null when an intent cannot be read (fail closed).
    /// </summary>
    public static List<OosEntry>? Chain(IReadOnlyList<PolicyTransactionRow> transactions)
    {
        var chain = new List<OosEntry>();
        foreach (var transaction in transactions.Skip(1))
        {
            if (TryRead(transaction.Intent) is { } oos)
            {
                chain = [.. oos.Chain.Select(e => new OosEntry(e.EffectiveAt, ToRates(e.Rates), e.RiskTree, e.Source))];
                continue;
            }

            var rates = ChangeJson.Rates(transaction.Intent, out var effectiveAt);
            if (rates is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(transaction.Intent);
            if (!document.RootElement.TryGetProperty("riskTree", out var tree))
            {
                return null;
            }

            chain.Add(new OosEntry(effectiveAt, rates, JobSupport.FromJson<RiskTree>(tree.GetRawText()), transaction.TransactionId.Value));
        }

        return chain;
    }
}

/// <summary>Carries an out-of-sequence edit of a vehicle's rating fields over to the later states of the risk.</summary>
internal static class OosTrees
{
    private static readonly (string Field, Func<Vehicle, string?> Get, Func<Vehicle, Vehicle, Vehicle> Copy)[] Fields =
    [
        ("firstRegistrationYear", v => v.FirstRegistrationYear?.ToString(CultureInfo.InvariantCulture), (to, from) => to with { FirstRegistrationYear = from.FirstRegistrationYear }),
        ("engineCapacityCc", v => v.EngineCapacityCc?.ToString(CultureInfo.InvariantCulture), (to, from) => to with { EngineCapacityCc = from.EngineCapacityCc }),
        ("use", v => v.Use, (to, from) => to with { Use = from.Use }),
        ("value", v => v.Value is { } m ? m.ToString() : null, (to, from) => to with { Value = from.Value }),
    ];

    /// <summary>
    /// The later risk with the edit applied: a field the new change moved from <c>before</c> to <c>after</c> takes the new value in every later
    /// state that still holds <c>before</c>; a later change of the same field stands (it was made after this one took effect). Null for an
    /// edit that adds or removes a vehicle (not supported out of sequence).
    /// </summary>
    public static RiskTree? Propagate(IReadOnlyList<DiffEntry> diff, RiskTree edited, RiskTree later)
    {
        if (diff.Any(d => d.Field == "vehicle"))
        {
            return null;
        }

        var vehicles = later.Vehicles.ToList();
        foreach (var entry in diff)
        {
            var index = vehicles.FindIndex(v => v.Locator == entry.Element);
            var source = edited.Vehicles.FirstOrDefault(v => v.Locator == entry.Element);
            var field = Array.Find(Fields, f => f.Field == entry.Field);
            if (index < 0 || source is null || field.Get is null)
            {
                continue;
            }

            if (string.Equals(field.Get(vehicles[index]), entry.Before, StringComparison.Ordinal))
            {
                vehicles[index] = field.Copy(vehicles[index], source);
            }
        }

        return later with { Vehicles = vehicles };
    }

    /// <summary>True when two risk trees serialise the same.</summary>
    public static bool Same(RiskTree a, RiskTree b) => JobSupport.Json(a) == JobSupport.Json(b);
}
