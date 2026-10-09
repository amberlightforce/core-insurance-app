using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Market.Domain;

/// <summary>Server-owned frozen content bound to the PLT approval (REQ-MKT-137, PITFALLS 3–5).
/// The final execution window ends at the checker's immediate decision, not the earlier preview instant.</summary>
internal sealed record PackActivationProof(
    string Pack, string Entity, string From, string To, string Kind, string Reason,
    ConfigurationHash Parent, Guid SourceActivation, Sha256Hash TargetDigest, string Maker, string? Principal)
{
    public Sha256Hash Hash => CanonicalJson.Hash(ToJson());

    public JsonObject ToJson() => new()
    {
        ["pack"] = Pack, ["entity"] = Entity, ["from"] = From, ["to"] = To,
        ["kind"] = Kind, ["reason"] = Reason, ["parent"] = Parent.ToString(),
        ["sourceActivation"] = SourceActivation.ToString("D"), ["targetDigest"] = TargetDigest.ToString(),
        ["maker"] = Maker, ["principal"] = Principal,
    };

    /// <summary>Compare the complete immutable value histories by key, including validity and legal status.
    /// Only key names leave the module in the preview; value data is never copied to audit.</summary>
    public static IReadOnlyList<PackActivationKeyDiff> KeyDiff(IReadOnlyList<PackConfigValue> from, IReadOnlyList<PackConfigValue> to)
    {
        var before = from.GroupBy(v => v.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => PackVersionContent.DigestOf(g.ToList()), StringComparer.Ordinal);
        var after = to.GroupBy(v => v.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => PackVersionContent.DigestOf(g.ToList()), StringComparer.Ordinal);
        return before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(k => !before.ContainsKey(k) || !after.ContainsKey(k) || before[k] != after[k])
            .Select(k => new PackActivationKeyDiff
            {
                Key = k,
                Change = !before.ContainsKey(k) ? PackActivationKeyDiff.ChangeValue.Added
                    : !after.ContainsKey(k) ? PackActivationKeyDiff.ChangeValue.Removed : PackActivationKeyDiff.ChangeValue.Changed,
            }).ToList();
    }
}
