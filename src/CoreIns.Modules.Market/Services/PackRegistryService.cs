using System.Text.Json;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Market.Services;

/// <summary>
/// <c>mkt.Pack.list</c> and <c>mkt.Pack.get</c> (REQ-MKT-003 registry subset): the registered pack versions with their content digests,
/// the activations per legal entity, and the version in force in the current configuration state. Reads only; the core defaults are not a
/// pack and are not listed. The generated item type carries one JSON object (<c>packVersionActivation</c>) until SL5-CONTRACTS-PACKS types it.
/// </summary>
internal sealed class PackRegistryService(MarketDbContext db)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PackListPage> ListAsync(string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var all = await db.PackVersions.AsNoTracking().Where(v => v.PackId != CoreDefaults.PackId)
            .Select(v => v.PackId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = all.Where(id => cursor is null || string.CompareOrdinal(id, cursor) > 0).Order(StringComparer.Ordinal).Take(take + 1).ToList();
        var page = ids.Take(take).ToList();
        var items = new List<PackListItem>();
        foreach (var id in page)
        {
            items.Add(new PackListItem { PackVersionActivation = await DescribeAsync(id, cancellationToken).ConfigureAwait(false) });
        }

        return new PackListPage { Items = items, NextCursor = ids.Count > take ? page[^1] : null, Limit = take };
    }

    public async Task<PackGetResponse> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (id == CoreDefaults.PackId || !await db.PackVersions.AnyAsync(v => v.PackId == id, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(DomainError.Of(ModuleCode.MKT, "PACK-NOT-FOUND", $"No pack '{id}' is registered."));
        }

        return new PackGetResponse { PackVersionActivation = await DescribeAsync(id, cancellationToken).ConfigureAwait(false) };
    }

    private async Task<JsonElement> DescribeAsync(string packId, CancellationToken cancellationToken)
    {
        var versions = await db.PackVersions.AsNoTracking().Where(v => v.PackId == packId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var activations = await db.PackActivations.AsNoTracking().Where(a => a.PackId == packId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var newest = await db.ConfigStates.AsNoTracking().OrderByDescending(s => s.Seq).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var inForce = newest is null
            ? null
            : ConfigStateManifest.FromJson(System.Text.Json.Nodes.JsonNode.Parse(newest.Manifest)).Packs.FirstOrDefault(p => p.PackId == packId);
        return JsonSerializer.SerializeToElement(new
        {
            packId,
            country = versions.Select(v => v.Country).FirstOrDefault(),
            versionInForce = inForce?.Version,
            stateHash = newest?.Hash,
            versions = versions
                .OrderBy(v => Semver(v.Version))
                .Select(v => new { version = v.Version, contentDigest = v.ContentDigest.Trim(), status = v.Status, registeredAt = v.RegisteredAt.ToString() }),
            activations = activations
                .OrderBy(a => a.CreatedAt)
                .Select(a => new
                {
                    id = a.Id,
                    legalEntityId = a.LegalEntityId.Value,
                    version = a.Version,
                    kind = a.Kind,
                    status = a.Status,
                    requestedBy = a.RequestedBy,
                    decidedBy = a.DecidedBy,
                    activatedAt = a.ActivatedAt?.ToString(),
                    resultingHash = a.ResultingHash?.Trim(),
                    supersedesId = a.SupersedesId,
                }),
        }, Json);
    }

    private static (int Major, int Minor, int Patch) Semver(string version)
    {
        var parts = version.Split('.');
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
    }
}
