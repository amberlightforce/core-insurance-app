using CoreIns.Modules.Reinsurance.Cession;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>
/// The approved premium-cession treaties in force (NX-HD-07). The cession intake reads them here, never from configuration:
/// only APPROVED or ACTIVE versions of the caller's legal entity that are still known (<c>known_to</c> null) count. The
/// <c>Reinsurance:Cession</c> configuration is a dev seed: a configured treaty absent from the registry is inserted once as an
/// approved DEV_SEED row and is never updated afterwards. TODO(hardening): maker-checker API for registering treaties
/// (the XoL contract registry does not model quota-share yet).
/// </summary>
internal sealed class CessionTreatyRegistry(
    ReinsuranceDbContext db,
    IOptions<CessionOptions> options,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock)
{
    private static readonly string[] InForceStatuses = ["APPROVED", "ACTIVE"];

    private Guid LegalEntityId => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    /// <summary>The treaties in force whose scope names the coverage; the caller applies the period (<c>version-at</c>) to the premium line.</summary>
    public async Task<IReadOnlyList<CessionTreatyRow>> ForCoverageAsync(string coverageCode, CancellationToken cancellationToken)
    {
        await SeedAsync(cancellationToken).ConfigureAwait(false);
        var legalEntity = LegalEntityId;
        return await db.CessionTreaties.AsNoTracking()
            .Where(t => t.LegalEntityId == legalEntity && t.KnownTo == null && InForceStatuses.Contains(t.Status) && t.CoverageCodes.Contains(coverageCode))
            .OrderBy(t => t.TreatyId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var cfg = options.Value;
        if (!cfg.SeedFromConfig || cfg.Treaties.Count == 0)
        {
            return;
        }

        var legalEntity = LegalEntityId;
        var configured = cfg.Treaties.Select(t => t.TreatyId).ToArray();
        var known = await KnownAsync(legalEntity, configured, cancellationToken).ConfigureAwait(false);
        if (known.Count == configured.Length)
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", [new NpgsqlParameter("k", $"ri:cession-treaty-seed:{legalEntity:D}")], cancellationToken).ConfigureAwait(false);
        known = await KnownAsync(legalEntity, configured, cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        foreach (var treaty in cfg.Treaties.Where(t => !known.Contains(t.TreatyId)))
        {
            db.CessionTreaties.Add(new CessionTreatyRow
            {
                TreatyVersionId = Guid.CreateVersion7(),
                LegalEntityId = legalEntity,
                TreatyId = treaty.TreatyId,
                Kind = treaty.Kind,
                ContractYear = treaty.ContractYear,
                SharePct = treaty.SharePct,
                CessionBase = cfg.Mode == CessionMode.Gross ? "GROSS" : "NET",
                Currency = "EUR",
                CoverageCodes = [.. treaty.CoverageCodes],
                ValidFrom = new(treaty.ValidFrom),
                ValidTo = new(treaty.ValidTo),
                Status = "APPROVED",
                Source = "DEV_SEED",
                ApprovedBy = "dev-seed",
                ApprovedAt = now,
                KnownFrom = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> KnownAsync(Guid legalEntity, string[] treatyIds, CancellationToken cancellationToken) =>
        [.. await db.CessionTreaties.AsNoTracking().Where(t => t.LegalEntityId == legalEntity && t.KnownTo == null && treatyIds.Contains(t.TreatyId))
            .Select(t => t.TreatyId).ToListAsync(cancellationToken).ConfigureAwait(false)];
}
