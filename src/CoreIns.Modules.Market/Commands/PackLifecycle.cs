using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Market.Commands;

/// <summary>
/// What a pack version's values say about Production (NX-EC-12, <c>docs/runbooks/regulatory-go-live-gates.md</c>): the keys whose legal status is not
/// Settled or NotRegulatory, and whether the pack is synthetic (the CY stub). Pure; fail closed.
/// </summary>
internal sealed record PackSettlement(IReadOnlyList<string> UnverifiedKeys, bool Synthetic)
{
    /// <summary>Packs that exist only to prove the core holds no country assumption; they never run in Production (D-REG-05).</summary>
    private static readonly HashSet<string> SyntheticPackIds = new(StringComparer.Ordinal) { "cy-stub" };

    public bool ProductionEligible => !Synthetic && UnverifiedKeys.Count == 0;

    public static PackSettlement Assess(string packId, IReadOnlyList<PackConfigValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var unverified = values.Where(v => v.LegalStatus is not (LegalStatus.Settled or LegalStatus.NotRegulatory)).Select(v => v.Key).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        var synthetic = SyntheticPackIds.Contains(packId) || values.Any(v => v.SourceRef.StartsWith("SYNTHETIC", StringComparison.OrdinalIgnoreCase));
        return new PackSettlement(unverified, synthetic);
    }
}

/// <summary>
/// The lifecycle of a registered pack version (BO-MKT-04, PRD-17 7.3.2): Built or Signed, Certified, Published, Deprecated. <c>pack_version</c> is append-only,
/// so each step is a row in <c>pack_version_event</c> and the current status is the newest event (the registered status when there is none).
/// </summary>
internal static class PackLifecycle
{
    public const string Built = "Built";
    public const string Signed = "Signed";
    public const string Certified = "Certified";
    public const string Rejected = "Rejected";
    public const string Published = "Published";
    public const string Deprecated = "Deprecated";
    public const string Production = "PRODUCTION";
    public const string NonProduction = "NON_PRODUCTION";

    /// <summary>The effective status of each version of a pack, and the instant of the newest publish step.</summary>
    public static async Task<Dictionary<string, (string Status, Instant? PublishedAt)>> StatusesAsync(MarketDbContext db, string packId, CancellationToken ct)
    {
        var rows = await db.PackVersions.AsNoTracking().Where(v => v.PackId == packId).Select(v => new { v.Version, v.Status }).ToListAsync(ct).ConfigureAwait(false);
        var events = await db.PackVersionEvents.AsNoTracking().Where(e => e.PackId == packId).OrderBy(e => e.Seq).ToListAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, (string, Instant?)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var own = events.Where(e => e.Version == row.Version).ToList();
            Instant? published = own.LastOrDefault(e => e.Status == Published)?.At;
            result[row.Version] = (own.Count > 0 ? own[^1].Status : row.Status, published);
        }

        return result;
    }

    public static async Task<string> StatusAsync(MarketDbContext db, string packId, string version, CancellationToken ct)
    {
        var last = await db.PackVersionEvents.AsNoTracking().Where(e => e.PackId == packId && e.Version == version).OrderByDescending(e => e.Seq)
            .Select(e => e.Status).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (last is not null)
        {
            return last;
        }

        return await db.PackVersions.AsNoTracking().Where(v => v.PackId == packId && v.Version == version).Select(v => v.Status).SingleAsync(ct).ConfigureAwait(false);
    }
}

internal sealed record CertifyPack(PackCertifyRequest Request) : ICommand<PackCertifyResponse>;

internal sealed record PublishPack(PackPublishRequest Request) : ICommand<PackPublishResponse>;

internal sealed record DeprecatePack(PackDeprecateRequest Request) : ICommand<PackDeprecateResponse>;

/// <summary>Certify, publish and deprecate. Every step joins the command transaction and takes the state lock, so it cannot race an activation of the same version.</summary>
internal sealed class PackLifecycleEngine(MarketDbContext db, DbSession session, RequestContext context, IClock clock, IHostEnvironment environment)
{
    private static DomainException Refuse(string code, string detail) => new(DomainError.Of(ModuleCode.MKT, code, detail));

    private bool ProductionTarget(string target) => target == PackLifecycle.Production || environment.IsProduction();

    private async Task<(PackVersionRow Row, string Status, List<PackConfigValue> Values)> LoadAsync(string pack, string version, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pack) || string.IsNullOrWhiteSpace(version) || reason is null || reason.Trim().Length < 20 || reason.Length > 128)
        {
            throw Refuse("PACK-VALIDATION", "Pack, version and a reason of 20 to 128 characters are required.");
        }

        if (context.Actor.Kind != ActorKind.User)
        {
            throw Refuse("SOD-VIOLATION", "A human release manager must take pack lifecycle steps.");
        }

        await ConfigStateWriter.LockAsync(session.Connection, session.Transaction ?? throw new InvalidOperationException("Pack lifecycle requires a command transaction."), ct)
            .ConfigureAwait(false);
        var row = await db.PackVersions.AsNoTracking().SingleOrDefaultAsync(v => v.PackId == pack && v.Version == version && v.PackId != CoreDefaults.PackId, ct).ConfigureAwait(false)
            ?? throw Refuse("PACK-VERSION-UNKNOWN", $"Pack '{pack}' has no registered version '{version}'.");
        var values = PackVersionContent.FromJson(JsonNode.Parse(row.Values));
        if (PackVersionContent.DigestOf(values).ToString() != row.ContentDigest.Trim())
        {
            throw Refuse("STALE", "The pack version failed its integrity check.");
        }

        return (row, await PackLifecycle.StatusAsync(db, pack, version, ct).ConfigureAwait(false), values);
    }

    private PackVersionEventRow Add(string pack, string version, string status, string target, bool eligible, object detail, string reason)
    {
        var e = new PackVersionEventRow
        {
            Id = Guid.CreateVersion7(), PackId = pack, Version = version, Status = status, TargetEnvironment = target, ProductionEligible = eligible,
            Detail = JsonSerializer.Serialize(detail), Reason = reason.Trim(), Actor = ActivationSupport.Actor(context.Actor), At = clock.Now,
        };
        db.PackVersionEvents.Add(e);
        return e;
    }

    public async Task<PackCertifyResponse> CertifyAsync(PackCertifyRequest r, CancellationToken ct)
    {
        var suite = r.GoldenSuite ?? throw Refuse("PACK-VALIDATION", "A golden-suite result is required.");
        if (suite.Total < 1 || suite.Passed < 0 || suite.Passed > suite.Total || string.IsNullOrWhiteSpace(suite.Suite))
        {
            throw Refuse("PACK-VALIDATION", "The golden-suite result needs a name, a positive total and a passed count between 0 and the total.");
        }

        var (_, status, values) = await LoadAsync(r.Pack, r.Version, r.Reason, ct).ConfigureAwait(false);
        if (status is not (PackLifecycle.Built or PackLifecycle.Signed or PackLifecycle.Rejected))
        {
            throw Refuse("PACK-LIFECYCLE", $"{r.Pack}@{r.Version} is {status}; only a Built, Signed or Rejected version can be certified.");
        }

        var target = r.TargetEnvironment == PackCertifyRequest.TargetEnvironmentValue.Production ? PackLifecycle.Production : PackLifecycle.NonProduction;
        var settlement = PackSettlement.Assess(r.Pack, values);
        if (ProductionTarget(target))
        {
            if (settlement.Synthetic)
            {
                throw Refuse("PACK-SYNTHETIC", $"{r.Pack} is a synthetic pack and is never certified for Production.");
            }

            if (settlement.UnverifiedKeys.Count > 0)
            {
                throw Refuse("PACK-NOT-SETTLED", $"{settlement.UnverifiedKeys.Count} value(s) are not Settled ({string.Join(", ", settlement.UnverifiedKeys.Take(5))}); Production certification refuses them.");
            }
        }
        else if (settlement.UnverifiedKeys.Count > 0 && r.AllowUnverified != true)
        {
            throw Refuse("PACK-NOT-SETTLED", $"{settlement.UnverifiedKeys.Count} value(s) are not Settled ({string.Join(", ", settlement.UnverifiedKeys.Take(5))}); set allowUnverified to certify for a non-production target.");
        }

        var failed = suite.Passed < suite.Total || (suite.FailedCases?.Count ?? 0) > 0;
        var outcome = failed ? PackLifecycle.Rejected : PackLifecycle.Certified;
        var eligible = !failed && settlement.ProductionEligible;
        Add(r.Pack, r.Version, outcome, target, eligible, new
        {
            goldenSuite = new { suite = suite.Suite, total = suite.Total, passed = suite.Passed, failedCases = suite.FailedCases ?? [] },
            unverifiedKeys = settlement.UnverifiedKeys,
            synthetic = settlement.Synthetic,
            allowUnverified = r.AllowUnverified == true,
        }, r.Reason);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PackCertifyResponse
        {
            Pack = r.Pack, Version = r.Version, Status = failed ? PackVersionStatus.Rejected : PackVersionStatus.Certified, ProductionEligible = eligible,
            UnverifiedKeys = settlement.UnverifiedKeys, GoldenSuite = suite,
        };
    }

    public async Task<PackPublishResponse> PublishAsync(PackPublishRequest r, CancellationToken ct)
    {
        var (_, status, values) = await LoadAsync(r.Pack, r.Version, r.Reason, ct).ConfigureAwait(false);
        if (status != PackLifecycle.Certified)
        {
            throw Refuse("PACK-LIFECYCLE", $"{r.Pack}@{r.Version} is {status}; only a Certified version can be published.");
        }

        var target = r.TargetEnvironment == PackPublishRequest.TargetEnvironmentValue.Production ? PackLifecycle.Production : PackLifecycle.NonProduction;
        var certified = await db.PackVersionEvents.AsNoTracking().Where(e => e.PackId == r.Pack && e.Version == r.Version && e.Status == PackLifecycle.Certified)
            .OrderByDescending(e => e.Seq).FirstAsync(ct).ConfigureAwait(false);
        var settlement = PackSettlement.Assess(r.Pack, values);

        // Fail closed: the certification flag and the values themselves must both say eligible.
        var eligible = certified.ProductionEligible && settlement.ProductionEligible;
        if (ProductionTarget(target))
        {
            if (settlement.Synthetic)
            {
                throw Refuse("PACK-SYNTHETIC", $"{r.Pack} is a synthetic pack and is never published to Production.");
            }

            if (!eligible)
            {
                throw Refuse("PACK-NOT-SETTLED", "The certification is not production-eligible: it holds values that are not Settled. Production publication is refused.");
            }
        }

        var e = Add(r.Pack, r.Version, PackLifecycle.Published, target, eligible, new { certifiedAt = certified.At }, r.Reason);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PackPublishResponse { Pack = r.Pack, Version = r.Version, Status = PackVersionStatus.Published, ProductionEligible = eligible, PublishedAt = e.At };
    }

    public async Task<PackDeprecateResponse> DeprecateAsync(PackDeprecateRequest r, CancellationToken ct)
    {
        var (_, status, _) = await LoadAsync(r.Pack, r.Version, r.Reason, ct).ConfigureAwait(false);
        if (status != PackLifecycle.Published)
        {
            throw Refuse("PACK-LIFECYCLE", $"{r.Pack}@{r.Version} is {status}; only a Published version can be deprecated.");
        }

        var live = await db.PackActivations.AsNoTracking().AnyAsync(a => a.PackId == r.Pack && a.Version == r.Version
            && (a.Status == "ACTIVE" || a.Status == "SCHEDULED" || a.Status == "PENDING_APPROVAL" || a.Status == "REQUESTED"), ct).ConfigureAwait(false);
        if (live)
        {
            throw Refuse("PACK-IN-USE", $"{r.Pack}@{r.Version} has a live or pending activation; activate another version first.");
        }

        var published = await db.PackVersionEvents.AsNoTracking().Where(e => e.PackId == r.Pack && e.Version == r.Version && e.Status == PackLifecycle.Published)
            .OrderByDescending(e => e.Seq).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        Add(r.Pack, r.Version, PackLifecycle.Deprecated, published?.TargetEnvironment ?? PackLifecycle.NonProduction, published?.ProductionEligible ?? false, new { }, r.Reason);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PackDeprecateResponse { Pack = r.Pack, Version = r.Version, Status = PackVersionStatus.Deprecated };
    }
}

internal sealed class CertifyPackHandler(PackLifecycleEngine engine) : ICommandHandler<CertifyPack, PackCertifyResponse>
{
    public async Task<Result<PackCertifyResponse>> HandleAsync(CertifyPack c, CancellationToken ct)
    {
        try { return await engine.CertifyAsync(c.Request, ct).ConfigureAwait(false); }
        catch (DomainException ex) { return ex.Error; }
    }
}

internal sealed class PublishPackHandler(PackLifecycleEngine engine) : ICommandHandler<PublishPack, PackPublishResponse>
{
    public async Task<Result<PackPublishResponse>> HandleAsync(PublishPack c, CancellationToken ct)
    {
        try { return await engine.PublishAsync(c.Request, ct).ConfigureAwait(false); }
        catch (DomainException ex) { return ex.Error; }
    }
}

internal sealed class DeprecatePackHandler(PackLifecycleEngine engine) : ICommandHandler<DeprecatePack, PackDeprecateResponse>
{
    public async Task<Result<PackDeprecateResponse>> HandleAsync(DeprecatePack c, CancellationToken ct)
    {
        try { return await engine.DeprecateAsync(c.Request, ct).ConfigureAwait(false); }
        catch (DomainException ex) { return ex.Error; }
    }
}

internal sealed class CertifyPackAuditor : ICommandAuditor<CertifyPack, PackCertifyResponse>
{
    public CommandAuditFacts Describe(CertifyPack c, Result<PackCertifyResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.MKT, "Pack", c.Request.Pack),
        BusinessKeys = BusinessKeys.Empty.With("pack", c.Request.Pack).With("version", c.Request.Version),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(new { status = "BUILT" }, new { status = ok.Value.Status.ToString(), productionEligible = ok.Value.ProductionEligible }) : [],
    };
}

internal sealed class PublishPackAuditor : ICommandAuditor<PublishPack, PackPublishResponse>
{
    public CommandAuditFacts Describe(PublishPack c, Result<PackPublishResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.MKT, "Pack", c.Request.Pack),
        BusinessKeys = BusinessKeys.Empty.With("pack", c.Request.Pack).With("version", c.Request.Version),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(new { status = "CERTIFIED" }, new { status = ok.Value.Status.ToString(), productionEligible = ok.Value.ProductionEligible }) : [],
    };
}

internal sealed class DeprecatePackAuditor : ICommandAuditor<DeprecatePack, PackDeprecateResponse>
{
    public CommandAuditFacts Describe(DeprecatePack c, Result<PackDeprecateResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.MKT, "Pack", c.Request.Pack),
        BusinessKeys = BusinessKeys.Empty.With("pack", c.Request.Pack).With("version", c.Request.Version),
        Changes = result is { IsSuccess: true } ok ? AuditDiff.Compute(new { status = "PUBLISHED" }, new { status = ok.Value.Status.ToString() }) : [],
    };
}
