using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>
/// Bitemporal resolution of a contract's versions (BO-RI-03). Version 1 is the original; each approved endorsement is a
/// further row valid from its own date to the treaty's end. The version in force at a valid date is the highest version
/// number whose valid-from has been reached, and an endorsement counts from the instant it was approved (its record
/// time), so asking "as known at" an earlier instant returns the earlier version and nothing already booked is restated.
/// </summary>
internal static class ContractVersions
{
    /// <summary>When a version became known to the registry: its approval, else its creation.</summary>
    public static Instant KnownFromOf(ContractVersionRow version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.ApprovedAt ?? version.KnownFrom;
    }

    /// <summary>
    /// The version in force at <paramref name="validAt"/> as known at <paramref name="knownAt"/>; null when none is.
    /// <paramref name="approvedOnly"/> leaves out a version 1 that is not approved yet.
    /// </summary>
    public static async Task<ContractVersionRow?> InForceAsync(
        ReinsuranceDbContext db, RiContractId contractId, BusinessDate validAt, Instant knownAt, bool approvedOnly, CancellationToken cancellationToken)
    {
        var rows = await Candidates(db, contractId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .Where(v => (!approvedOnly || v.ApprovedAt is not null) && KnownFromOf(v) <= knownAt && v.ValidFrom <= validAt && validAt < v.ValidTo)
            .OrderByDescending(v => v.VersionNo)
            .FirstOrDefault();
    }

    /// <summary>
    /// The version to show for the contract now: the latest one whose valid-from has been reached (also after the treaty
    /// expired), else version 1 (a draft, or a treaty that has not started).
    /// </summary>
    public static async Task<ContractVersionRow> CurrentAsync(
        ReinsuranceDbContext db, RiContractId contractId, BusinessDate today, Instant now, CancellationToken cancellationToken)
    {
        var rows = await Candidates(db, contractId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Where(v => KnownFromOf(v) <= now && v.ValidFrom <= today).OrderByDescending(v => v.VersionNo).FirstOrDefault()
            ?? rows.OrderBy(v => v.VersionNo).First();
    }

    /// <summary>The latest approved (or original) version, tracked: the content an endorsement starts from.</summary>
    public static async Task<ContractVersionRow> LatestAsync(ReinsuranceDbContext db, RiContractId contractId, CancellationToken cancellationToken)
    {
        var rows = await db.Versions.Where(v => v.ContractId == contractId && (v.VersionNo == 1 || v.EndorsementStatus == "APPROVED")).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderByDescending(v => v.VersionNo).First();
    }

    private static IQueryable<ContractVersionRow> Candidates(ReinsuranceDbContext db, RiContractId contractId) =>
        db.Versions.AsNoTracking().Where(v => v.ContractId == contractId && (v.VersionNo == 1 || v.EndorsementStatus == "APPROVED"));
}

/// <summary><c>ri.Contract.versionAt</c>: the contract version in force at a valid date as known at a record instant.</summary>
internal sealed class ContractVersionQuery(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IOptions<ReinsuranceOptions> options)
{
    /// <summary>The version view, or <c>RI-ERR-NOT-FOUND</c> when the contract is not in the caller's legal entity or no approved version is in force then.</summary>
    public async Task<Result<ContractVersionAtResponse>> VersionAtAsync(RiContractId contractId, ValidAt? validAt, Instant? knownAt, CancellationToken cancellationToken)
    {
        var legalEntityCode = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var legalEntity = legalEntities.Resolve(legalEntityCode);
        var contract = await db.Contracts.AsNoTracking().SingleOrDefaultAsync(c => c.ContractId == contractId && c.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (contract is null)
        {
            return RiErrors.NotFound();
        }

        var zone = options.Value.Zone;
        var known = knownAt ?? clock.Now;
        var date = validAt switch
        {
            { Date: { } d } => d,
            { Instant: { } i } => i.ToBusinessDate(zone),
            _ => clock.Now.ToBusinessDate(zone),
        };
        var version = await ContractVersions.InForceAsync(db, contractId, date, known, approvedOnly: true, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return RiErrors.NotFound();
        }

        var content = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
        return new ContractVersionAtResponse
        {
            Contract = ContractSupport.ToView(contract, version, content, legalEntityCode.Value, zone),
            ValidAt = date,
            KnownAt = known,
            KnownFrom = ContractVersions.KnownFromOf(version),
        };
    }
}
