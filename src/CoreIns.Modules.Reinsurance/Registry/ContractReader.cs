using System.Globalization;
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
/// The registry queries (<c>ri.Contract.get / list / applicable</c>, REQ-RI-001). Every query is filtered by the caller's
/// legal entity: another entity's contract is "not found", never an error that confirms it exists. Reads never lock.
/// </summary>
internal sealed class ContractReader(
    ReinsuranceDbContext db,
    RequestContext context,
    IClock clock,
    ILegalEntityDirectory legalEntities,
    IOptions<ReinsuranceOptions> options)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private string LegalEntityCode => (context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;

    /// <summary><c>ri.Contract.get</c>: null when the contract is not in the caller's legal entity.</summary>
    public async Task<RiContractView?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var contract = await db.Contracts.AsNoTracking().SingleOrDefaultAsync(c => c.ContractId == new RiContractId(id) && c.LegalEntityId == LegalEntity, cancellationToken).ConfigureAwait(false);
        return contract is null ? null : await ViewAsync(contract, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>ri.Contract.list</c>, newest first, offset cursor.</summary>
    public async Task<Result<ContractListPage>> ListAsync(
        string? cursor, int? limit, int? contractYear, RiContractStatus? status, RiContractType? contractType, CancellationToken cancellationToken)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            return RiErrors.Validation("limit", "LIMIT_RANGE", $"The limit is between 1 and {MaxLimit}.");
        }

        var offset = 0;
        if (cursor is not null && !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
        {
            return RiErrors.Validation("cursor", "CURSOR", "The cursor is not valid.");
        }

        var legalEntity = LegalEntity;
        var query = db.Contracts.AsNoTracking().Where(c => c.LegalEntityId == legalEntity);
        if (contractYear is { } year)
        {
            query = query.Where(c => c.ContractYear == year);
        }

        if (status is { } wanted)
        {
            var code = ContractStateModel.Code(Status(wanted));
            query = query.Where(c => c.Status == code);
        }

        if (contractType is { } type)
        {
            // Slice 4 stores XOL_PER_RISK only; any other type filters to an empty page.
            query = type == RiContractType.XolPerRisk ? query.Where(c => c.ContractType == ContractRules.XolPerRisk) : query.Where(_ => false);
        }

        var rows = await (
                from c in query
                join v in db.Versions.AsNoTracking() on c.ContractId equals v.ContractId
                where v.KnownTo == null
                orderby c.CreatedAt descending, c.ContractId
                select new { c, v })
            .Skip(offset).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = rows.Take(size).Select(r => new ContractListItem
        {
            ContractId = r.c.ContractId,
            ContractNumber = r.c.ContractNumber,
            ContractType = RiContractType.XolPerRisk,
            ContractYear = r.c.ContractYear,
            Currency = Currency.FromCode(r.c.Currency.Trim()),
            Period = DateRange.Of(r.v.ValidFrom, r.v.ValidTo),
            PlacedPct = r.v.PlacedPct,
            Status = ContractSupport.Wire(ContractStateModel.Parse(r.c.Status)),
            RecordVersion = r.c.RecordVersion,
        }).ToList();
        return new ContractListPage
        {
            Items = items,
            NextCursor = rows.Count > size ? (offset + size).ToString(CultureInfo.InvariantCulture) : null,
            Limit = size,
        };
    }

    /// <summary>
    /// <c>ri.Contract.applicable</c>: the Active contracts whose period contains the loss, by Athens business date and
    /// half-open ([from, to)), and whose scope names both the product and the coverage (REQ-RI-001, -116). A date-form
    /// <c>validAt</c> is that Athens day (the end of it, D-SLC-13); an instant is converted to its Athens date; none
    /// means now. Nothing is guessed: an unknown product or coverage matches nothing.
    /// </summary>
    public async Task<Result<ContractApplicableResponse>> ApplicableAsync(string productCode, string coverageCode, ValidAt? validAt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return RiErrors.Validation("productCode", "PRODUCT_CODE_REQUIRED", "A product code is required.");
        }

        if (string.IsNullOrWhiteSpace(coverageCode))
        {
            return RiErrors.Validation("coverageCode", "COVERAGE_CODE_REQUIRED", "A coverage code is required.");
        }

        var zone = options.Value.Zone;
        var (lossDate, lossInstant) = Resolve(validAt, zone);
        var legalEntity = LegalEntity;
        var active = ContractStateModel.Code(ContractStatus.Active);
        var contracts = await (
                from c in db.Contracts.AsNoTracking()
                join v in db.Versions.AsNoTracking() on c.ContractId equals v.ContractId
                join s in db.Sections.AsNoTracking() on new { v.VersionId, Rev = v.ContentRev } equals new { s.VersionId, s.Rev }
                where c.LegalEntityId == legalEntity && c.Status == active && v.KnownTo == null
                    && v.ValidFrom <= lossDate && lossDate < v.ValidTo
                    && s.ProductCodes.Contains(productCode) && s.CoverageCodes.Contains(coverageCode)
                orderby c.ContractNumber
                select c)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var views = new List<RiContractView>();
        foreach (var contract in contracts)
        {
            views.Add(await ViewAsync(contract, cancellationToken).ConfigureAwait(false));
        }

        return new ContractApplicableResponse { Contracts = views, ValidAt = lossInstant };
    }

    private async Task<RiContractView> ViewAsync(ContractRow contract, CancellationToken cancellationToken)
    {
        var version = await db.Versions.AsNoTracking().SingleAsync(v => v.ContractId == contract.ContractId && v.KnownTo == null, cancellationToken).ConfigureAwait(false);
        var content = await ContractSupport.ReadContentAsync(db, contract, version, cancellationToken).ConfigureAwait(false);
        return ContractSupport.ToView(contract, version, content, LegalEntityCode, options.Value.Zone);
    }

    private (BusinessDate Date, Instant Instant) Resolve(ValidAt? validAt, TimeZoneInfo zone)
    {
        if (validAt is { Date: { } date })
        {
            // The end of that Athens business day: the last tick before the next local midnight.
            var next = new BusinessDate(date.Value.AddDays(1));
            return (date, ContractSupport.StartOf(next, zone).Minus(TimeSpan.FromTicks(1)));
        }

        var instant = validAt?.Instant ?? clock.Now;
        return (instant.ToBusinessDate(zone), instant);
    }

    private static ContractStatus Status(RiContractStatus status) => status switch
    {
        RiContractStatus.Draft => ContractStatus.Draft,
        RiContractStatus.PendingApproval => ContractStatus.PendingApproval,
        RiContractStatus.Approved => ContractStatus.Approved,
        RiContractStatus.Active => ContractStatus.Active,
        RiContractStatus.Expired => ContractStatus.Expired,
        RiContractStatus.Closed => ContractStatus.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
