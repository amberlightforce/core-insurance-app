using System.Text;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Modules.Market.Queries;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// <c>mkt.Configuration.explain</c>, <c>compare</c> and <c>export</c> (BO-MKT-03, REQ-MKT-063/064/065): read-only views over the recorded
/// configuration states. Explain shows every stored version of a key that could apply and which one wins; compare diffs two states (by hash)
/// or two business dates of one state; export gives one state as canonical JSON whose hash is the state hash.
/// </summary>
internal sealed class ConfigurationLineageService(ConfigurationEngine engine, LegalEntityRegistry registry, MarketDbContext db, IClock clock)
{
    private const string AppliedStatus = "APPLIED";

    public async Task<ConfigurationExplainResponse> ExplainAsync(
        string? key, string? context, BusinessDate? validAt, Sha256Hash? hash, CancellationToken cancellationToken)
    {
        var descriptor = !string.IsNullOrEmpty(key) && ConfigKeys.Find(key) is { } found
            ? found
            : throw Error("CFG-UNKNOWN-KEY", $"'{key}' is not a registered configuration key (REQ-MKT-033).");
        var entity = EntityOf(context);
        var state = await engine.StateAsync(hash is { } h ? new ConfigurationHash(h) : null, null, null, cancellationToken).ConfigureAwait(false);
        var date = validAt ?? clock.Now.ToBusinessDate(entity.Zone);
        var jurisdictionNode = "country:" + entity.HomeJurisdiction;
        var winner = state.Find(descriptor.Key, entity.HomeJurisdiction, date);
        var candidates = state.Entries
            .Where(e => e.Key == descriptor.Key && (e.IsCore || e.Node == jurisdictionNode))
            .OrderBy(e => e.IsCore ? 0 : 1)
            .ThenBy(e => e.ValidFrom ?? BusinessDate.MinValue)
            .ToList();

        var contributions = candidates.Select(e => new ConfigurationLayerContribution
        {
            Node = e.Node,
            Layer = e.IsCore ? ConfigurationLayerContribution.LayerValue.Core : ConfigurationLayerContribution.LayerValue.Country,
            Value = e.Value,
            LegalStatus = e.LegalStatus.ToString(),
            SourceRef = e.SourceRef,
            PackId = e.PackId,
            PackVersion = e.PackVersion,
            ValidFrom = e.ValidFrom,
            ValidTo = e.ValidTo,
            Applies = e.Contains(date),
            Winning = winner is not null && ReferenceEquals(e, winner),
        }).ToList();

        var steps = new List<string>();
        foreach (var e in candidates)
        {
            var layer = e.IsCore ? "core default" : $"{e.Node} ({e.PackId}@{e.PackVersion})";
            var window = $"{(e.ValidFrom is { } f ? f.ToString() : "since ever")} to {(e.ValidTo is { } t ? t.ToString() : "open")}";
            steps.Add($"{layer}: {e.Value} [{e.LegalStatus}] valid {window}{(e.Contains(date) ? string.Empty : ", not valid on " + date)}");
        }

        steps.Add(winner is null
            ? $"No layer holds a value of {descriptor.Key} valid on {date}."
            : $"Winner on {date}: {winner.Node}{(winner.IsCore ? string.Empty : " (the country layer REPLACEs the core default)")}.");

        ConfigurationExplainChangeRequest? cr = null;
        var approvers = new List<string>();
        if (winner is { PackId: { } packId } && ConfigOverlay.IsOverlay(packId))
        {
            var applied = await db.ConfigChangeRequests.AsNoTracking()
                .Where(r => r.Status == AppliedStatus && r.Country == entity.HomeJurisdiction && r.AppliedAt != null && r.AppliedAt <= state.ActivatedAt
                            && r.Items.Any(i => i.Key == descriptor.Key))
                .OrderByDescending(r => r.AppliedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (applied is not null)
            {
                cr = new ConfigurationExplainChangeRequest
                {
                    RequestId = applied.Id,
                    Reason = applied.Reason,
                    RequestedBy = applied.CreatedBy,
                    AppliedAt = applied.AppliedAt,
                    ResultingHash = applied.ResultingHash is { } r && Sha256Hash.TryParse(r.Trim(), out var parsed) ? parsed : null,
                };
                if (applied.DecidedBy is { } decider)
                {
                    approvers.Add(decider);
                }

                steps.Add($"Value set by change request {applied.Id} (requested by {applied.CreatedBy}).");
            }
        }

        return new ConfigurationExplainResponse
        {
            Key = descriptor.Key,
            Hash = state.Hash.Hash,
            ValidAt = date,
            LegalEntity = entity.Code.Value,
            Jurisdiction = entity.HomeJurisdiction,
            Found = winner is not null,
            Value = winner?.Value,
            Layer = winner?.Node,
            LegalStatus = winner?.LegalStatus.ToString(),
            PackId = winner?.PackId,
            PackVersion = winner?.PackVersion,
            SourceRef = winner?.SourceRef,
            LayerContributions = contributions,
            MergeSteps = steps,
            Cr = cr,
            Approvers = approvers,
        };
    }

    public async Task<ConfigurationCompareResponse> CompareAsync(
        Sha256Hash? hashA, Sha256Hash? hashB, BusinessDate? validAtA, BusinessDate? validAtB, string? filter, CancellationToken cancellationToken)
    {
        if ((validAtA is null) != (validAtB is null))
        {
            throw Error("CFG-VALIDATION", "A date compare needs both validAt and validAtB.");
        }

        var byDate = validAtA is not null;
        if (byDate && hashB is not null)
        {
            throw Error("CFG-VALIDATION", "Compare two hashes or two dates, not both.");
        }

        var stateA = await engine.StateAsync(hashA is { } a ? new ConfigurationHash(a) : null, null, null, cancellationToken).ConfigureAwait(false);
        var stateB = byDate
            ? stateA
            : await engine.StateAsync(hashB is { } b ? new ConfigurationHash(b) : null, null, null, cancellationToken).ConfigureAwait(false);

        var left = Index(stateA, validAtA, filter);
        var right = Index(stateB, validAtB, filter);
        var changes = new List<ConfigurationChange>();
        foreach (var id in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            left.TryGetValue(id, out var before);
            right.TryGetValue(id, out var after);
            if (before is not null && after is not null && Same(before, after))
            {
                continue;
            }

            var sample = (before ?? after)!;
            changes.Add(new ConfigurationChange
            {
                Key = sample.Key,
                Node = sample.Node,
                Change = before is null ? ConfigurationChange.ChangeValue.Added : after is null ? ConfigurationChange.ChangeValue.Removed : ConfigurationChange.ChangeValue.Changed,
                Before = before is null ? null : Summary(before),
                After = after is null ? null : Summary(after),
            });
        }

        return new ConfigurationCompareResponse
        {
            HashA = stateA.Hash.Hash,
            HashB = stateB.Hash.Hash,
            ValidAtA = validAtA,
            ValidAtB = validAtB,
            Changes = changes,
        };
    }

    public async Task<ConfigurationExportResponse> ExportAsync(ConfigurationExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = await engine.StateAsync(request.Hash is { } h ? new ConfigurationHash(h) : null, null, null, cancellationToken).ConfigureAwait(false);
        LegalEntityInfo? entity = string.IsNullOrWhiteSpace(request.Context) ? null : EntityOf(request.Context);
        var values = new JsonArray();
        foreach (var e in state.Entries
                     .Where(e => entity is null || e.IsCore || e.Node == "country:" + entity.HomeJurisdiction)
                     .OrderBy(e => e.Key, StringComparer.Ordinal).ThenBy(e => e.Node, StringComparer.Ordinal)
                     .ThenBy(e => e.ValidFrom?.ToString(), StringComparer.Ordinal))
        {
            values.Add(new JsonObject
            {
                ["key"] = e.Key,
                ["node"] = e.Node,
                ["type"] = e.Type.ToString(),
                ["value"] = e.Value,
                ["legalStatus"] = e.LegalStatus.ToString(),
                ["sourceRef"] = e.SourceRef,
                ["motorPath"] = e.MotorPath,
                ["validFrom"] = e.ValidFrom?.ToString(),
                ["validTo"] = e.ValidTo?.ToString(),
                ["packId"] = e.PackId,
                ["packVersion"] = e.PackVersion,
            });
        }

        var document = new JsonObject
        {
            ["hash"] = state.Hash.Hash.ToString(),
            ["activatedAt"] = state.ActivatedAt.ToString(),
            ["scope"] = entity is null ? "all" : entity.Code.Value,
            ["manifest"] = state.Manifest.ToJson(),
            ["values"] = values,
        };
        var canonical = CanonicalJson.Canonicalize(document);
        return new ConfigurationExportResponse
        {
            Hash = state.Hash.Hash,
            ActivatedAt = state.ActivatedAt,
            Cause = state.Manifest.Cause,
            ParentHash = state.Manifest.ParentHash?.Hash,
            CanonicalJson = Encoding.UTF8.GetString(canonical),
            DocumentDigest = Sha256Hash.Compute(canonical),

            // TODO(hardening): sign the canonical JSON once export signing keys exist (PRD: "signed canonical JSON").
            Signature = null,
        };
    }

    private static Dictionary<string, ConfigEntry> Index(ConfigurationCatalogue state, BusinessDate? date, string? filter)
    {
        var map = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal);
        foreach (var e in state.Entries)
        {
            if (!string.IsNullOrEmpty(filter) && !e.Key.StartsWith(filter, StringComparison.Ordinal))
            {
                continue;
            }

            if (date is { } at)
            {
                if (e.Contains(at))
                {
                    map[$"{e.Key}|{e.Node}"] = e;
                }
            }
            else
            {
                map[$"{e.Key}|{e.Node}|{e.ValidFrom}|{e.ValidTo}"] = e;
            }
        }

        return map;
    }

    private static bool Same(ConfigEntry a, ConfigEntry b) =>
        a.Value == b.Value && a.LegalStatus == b.LegalStatus && a.SourceRef == b.SourceRef && a.MotorPath == b.MotorPath
        && a.ValidFrom == b.ValidFrom && a.ValidTo == b.ValidTo;

    private static ConfigurationValueSummary Summary(ConfigEntry e) =>
        new()
        {
            Value = e.Value,
            LegalStatus = e.LegalStatus.ToString(),
            SourceRef = e.SourceRef,
            PackVersion = e.PackVersion,
            ValidFrom = e.ValidFrom,
            ValidTo = e.ValidTo,
        };

    private LegalEntityInfo EntityOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return registry.All().OrderBy(e => e.Code.Value, StringComparer.Ordinal).FirstOrDefault()
                ?? throw Error("CFG-LEGAL-ENTITY-UNKNOWN", "The legal-entity registry is empty.");
        }

        return LegalEntityCode.TryParse(code, out var parsed) && registry.Find(parsed) is { } entity
            ? entity
            : throw Error("CFG-LEGAL-ENTITY-UNKNOWN", $"Legal entity '{code}' is not in the registry (REQ-MKT-151).");
    }

    private static DomainException Error(string name, string detail) => new(DomainError.Of(ModuleCode.MKT, name, detail));
}
