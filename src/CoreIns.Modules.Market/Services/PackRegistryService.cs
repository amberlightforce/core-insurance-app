using System.Security.Cryptography;
using System.Text;
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
/// <c>mkt.Pack.list</c> and <c>mkt.Pack.get</c> (REQ-MKT-003 registry subset): the registered pack versions with their content digests, the version
/// active per legal entity (with the state hash it produced) and, on <c>get</c>, the activation history. Reads only; the core defaults are not a pack and
/// are not listed. A pack's id is a UUID derived from its code (the registry keys packs by code), so it is stable across stamps.
/// </summary>
internal sealed class PackRegistryService(MarketDbContext db)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const string GenesisReason = "Genesis: the newest version shipped with the release";

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
            var (versions, active, _, scope) = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            items.Add(new PackListItem
            {
                PackId = IdOf(id), Pack = id, Scope = (PackListItem.ScopeValue)scope, Versions = versions, ActiveVersions = active,
            });
        }

        return new PackListPage { Items = items, NextCursor = ids.Count > take ? page[^1] : null, Limit = take };
    }

    /// <summary>The pack with the given id: its UUID (derived from the code) or the code itself.</summary>
    public async Task<PackGetResponse> GetAsync(string id, CancellationToken cancellationToken)
    {
        var codes = await db.PackVersions.AsNoTracking().Where(v => v.PackId != CoreDefaults.PackId)
            .Select(v => v.PackId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var code = codes.FirstOrDefault(c => string.Equals(c, id, StringComparison.Ordinal) || string.Equals(IdOf(c).ToString(), id, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainException(DomainError.Of(ModuleCode.MKT, "PACK-NOT-FOUND", $"No pack '{id}' is registered."));
        var (versions, active, history, scope) = await LoadAsync(code, cancellationToken).ConfigureAwait(false);
        return new PackGetResponse
        {
            PackId = IdOf(code), Pack = code, Scope = (PackGetResponse.ScopeValue)scope, Versions = versions, ActiveVersions = active, ActivationHistory = history,
        };
    }

    private async Task<(List<PackVersionView> Versions, List<PackActiveVersionView> Active, List<PackActivationView> History, int Scope)> LoadAsync(
        string packId, CancellationToken cancellationToken)
    {
        var rows = await db.PackVersions.AsNoTracking().Where(v => v.PackId == packId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var activations = await db.PackActivations.AsNoTracking().Where(a => a.PackId == packId).OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var entities = await db.LegalEntities.AsNoTracking().ToDictionaryAsync(e => e.LegalEntityId, e => e.Code, cancellationToken).ConfigureAwait(false);
        string Entity(LegalEntityId id) => entities.TryGetValue(id, out var code) ? code : id.Value.ToString();

        var versions = rows.OrderBy(v => Semver(v.Version)).Select(v => new PackVersionView
        {
            Version = v.Version,
            Status = Enum.Parse<PackVersionStatus>(v.Status, ignoreCase: true),
            ContentDigest = Sha256Hash.Parse(v.ContentDigest.Trim()),
            PublishedAt = v.RegisteredAt,
        }).ToList();
        var active = activations.Where(a => a.Status == "ACTIVE" && a.ActivatedAt is not null && a.ResultingHash is not null).Select(a => new PackActiveVersionView
        {
            LegalEntity = Entity(a.LegalEntityId),
            Version = a.Version,
            ActivationId = a.Id,
            ActiveSince = a.ActivatedAt!.Value,
            ConfigurationHash = ConfigurationHash.Parse(a.ResultingHash!.Trim()),
        }).ToList();
        var history = activations.Select(a => new PackActivationView
        {
            ActivationId = a.Id,
            Pack = a.PackId,
            LegalEntity = Entity(a.LegalEntityId),
            Kind = a.Kind == "ROLLBACK" ? PackActivationView.KindValue.Rollback : PackActivationView.KindValue.Activate,
            From = a.FromVersion ?? activations.FirstOrDefault(p => p.Id == a.SupersedesId)?.Version,
            To = a.Version,
            Status = Status(a.Status),
            Reason = a.RequestedBy == "system:genesis" ? GenesisReason : a.Reason,
            RequestedBy = a.RequestedBy,
            DecidedBy = a.DecidedBy,
            ApprovalRequestId = a.ApprovalRequestId is { } approval ? new ApprovalRequestId(approval) : null,
            ActivatedAt = a.ActivatedAt,
            ResultingHash = a.ResultingHash is { } hash ? Sha256Hash.Parse(hash.Trim()) : null,
        }).ToList();

        // Every pack the slice ships populates a country layer (L3).
        return (versions, active, history, (int)PackListItem.ScopeValue.Country);
    }

    private static PackActivationStatus Status(string text) => text switch
    {
        "REQUESTED" => PackActivationStatus.Requested,
        "PENDING_APPROVAL" => PackActivationStatus.PendingApproval,
        "SCHEDULED" => PackActivationStatus.Scheduled,
        "ACTIVE" => PackActivationStatus.Active,
        "SUPERSEDED" => PackActivationStatus.Superseded,
        "WITHDRAWN" => PackActivationStatus.Withdrawn,
        "REJECTED" => PackActivationStatus.Rejected,
        _ => throw new InvalidOperationException($"Unknown activation status '{text}'."),
    };

    /// <summary>A stable UUID for a pack code: the first 16 bytes of SHA-256 over <c>pack:&lt;code&gt;</c>.</summary>
    internal static Guid IdOf(string packCode) => new(SHA256.HashData(Encoding.UTF8.GetBytes("pack:" + packCode)).AsSpan(0, 16));

    private static (int Major, int Minor, int Patch) Semver(string version)
    {
        var parts = version.Split('.');
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
    }
}
