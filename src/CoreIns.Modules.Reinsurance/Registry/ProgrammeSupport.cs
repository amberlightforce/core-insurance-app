using System.Globalization;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>Status codes of a programme version (the <c>ck_programme_version_status</c> values).</summary>
internal static class ProgrammeStatuses
{
    public const string Draft = "DRAFT";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Active = "ACTIVE";
    public const string Superseded = "SUPERSEDED";
    public const string Expired = "EXPIRED";

    public static RiProgrammeStatus Wire(string status) => status switch
    {
        Draft => RiProgrammeStatus.Draft,
        PendingApproval => RiProgrammeStatus.PendingApproval,
        Active => RiProgrammeStatus.Active,
        Superseded => RiProgrammeStatus.Superseded,
        Expired => RiProgrammeStatus.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>Loading, locking, checking and viewing the programme aggregate (BO-RI-01).</summary>
internal static class ProgrammeSupport
{
    public static DomainError NotFound() => DomainError.Of(ModuleCode.RI, "NOT-FOUND", "The reinsurance programme does not exist in your legal entity.");

    /// <summary>
    /// The programme of the caller's legal entity, tracked and row-locked (<c>FOR UPDATE</c>) until the transaction ends, so racing commands on one
    /// programme queue up and the loser sees the winner's record version (PITFALLS 15); null when not found.
    /// </summary>
    public static async Task<ProgrammeRow?> LockAsync(ReinsuranceDbContext db, LegalEntityId legalEntity, RiProgrammeId programmeId, CancellationToken cancellationToken)
    {
        var rows = await db.Programmes
            .FromSql($"SELECT * FROM ri.programme WHERE programme_id = {programmeId.Value} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Serialises programme writes of one legal entity and line, so two creates cannot both pass the overlap check.</summary>
    public static Task LockLineAsync(ReinsuranceDbContext db, LegalEntityId legalEntity, string lineCode, CancellationToken cancellationToken)
    {
        var key = "ri.programme:" + legalEntity.Value.ToString("D") + ":" + lineCode;
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    /// <summary>The current (highest) version of the programme, tracked.</summary>
    public static Task<ProgrammeVersionRow> CurrentVersionAsync(ReinsuranceDbContext db, ProgrammeRow programme, CancellationToken cancellationToken) =>
        db.ProgrammeVersions.SingleAsync(v => v.ProgrammeId == programme.ProgrammeId && v.VersionNo == programme.CurrentVersionNo, cancellationToken);

    /// <summary>The content of a version as stored.</summary>
    public static async Task<ProgrammeContent> ReadContentAsync(ReinsuranceDbContext db, ProgrammeVersionRow version, CancellationToken cancellationToken)
    {
        var layers = await db.ProgrammeLayers.AsNoTracking().Where(l => l.VersionId == version.VersionId).OrderBy(l => l.LayerNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ProgrammeContent(version.Name, version.ValidFrom, version.ValidTo, [.. layers.Select(l => new ProgrammeLayerContent(l.LayerNo, l.Attachment, l.LimitAmount, l.ContractId))]);
    }

    /// <summary>Adds the layer rows of <paramref name="content"/> to <paramref name="version"/>.</summary>
    public static void AddLayers(ReinsuranceDbContext db, ProgrammeVersionRow version, ProgrammeContent content)
    {
        foreach (var layer in content.Layers.OrderBy(l => l.LayerNo))
        {
            db.ProgrammeLayers.Add(new ProgrammeLayerRow
            {
                LayerId = Guid.CreateVersion7(), VersionId = version.VersionId, LayerNo = layer.LayerNo,
                Attachment = layer.Attachment, LimitAmount = layer.Limit, ContractId = layer.ContractId,
            });
        }
    }

    /// <summary>The content of a request's layers (EUR only) or the validation error.</summary>
    public static Result<IReadOnlyList<ProgrammeLayerContent>> Layers(IReadOnlyList<RiProgrammeLayerInput> layers)
    {
        var result = new List<ProgrammeLayerContent>();
        foreach (var (layer, index) in layers.Select((l, i) => (l, i)))
        {
            if (layer.Attachment.Currency.Code != ContractRules.Eur || layer.Limit.Currency.Code != ContractRules.Eur)
            {
                return RiErrors.Validation(FormattableString.Invariant($"layers[{index}]"), "CURRENCY_NOT_SUPPORTED", "Programme amounts are EUR only (D-SL4-04).");
            }

            result.Add(new ProgrammeLayerContent(layer.LayerNo, layer.Attachment.Amount, layer.Limit.Amount, layer.ContractId));
        }

        return result;
    }

    /// <summary>
    /// Checks the contracts of the ladder against the stored registry: each exists in the legal entity, is not expired or closed, covers the
    /// programme period with its treaty period, and has a layer with the same attachment and limit as the programme layer it carries.
    /// </summary>
    public static async Task<DomainError?> CheckContractsAsync(ReinsuranceDbContext db, LegalEntityId legalEntity, ProgrammeContent content, CancellationToken cancellationToken)
    {
        var ids = content.ContractIds;
        var contracts = await db.Contracts.AsNoTracking().Where(c => ids.Contains(c.ContractId) && c.LegalEntityId == legalEntity).ToDictionaryAsync(c => c.ContractId, cancellationToken).ConfigureAwait(false);
        var firstVersions = await db.Versions.AsNoTracking().Where(v => ids.Contains(v.ContractId) && v.VersionNo == 1).ToDictionaryAsync(v => v.ContractId, cancellationToken).ConfigureAwait(false);
        var errors = new List<FieldError>();
        void Add(string field, string code) => errors.Add(new FieldError(field, code, "ri." + code.ToLowerInvariant()));
        foreach (var (layer, index) in content.Layers.Select((l, i) => (l, i)))
        {
            var path = FormattableString.Invariant($"layers[{index}].contractId");
            if (!contracts.TryGetValue(layer.ContractId, out var contract))
            {
                Add(path, "CONTRACT_NOT_FOUND");
                continue;
            }

            var status = ContractStateModel.Parse(contract.Status);
            if (status == ContractStatus.Expired || contract.Status == "CLOSED")
            {
                Add(path, "CONTRACT_NOT_IN_FORCE");
                continue;
            }

            var first = firstVersions[layer.ContractId];
            if (first.ValidFrom > content.ValidFrom || first.ValidTo < content.ValidTo)
            {
                Add(path, "CONTRACT_PERIOD_DOES_NOT_COVER");
            }

            var current = await ContractVersions.LatestAsync(db, layer.ContractId, cancellationToken).ConfigureAwait(false);
            var carried = await db.Layers.AsNoTracking()
                .AnyAsync(l => l.VersionId == current.VersionId && l.Rev == current.ContentRev && l.Attachment == layer.Attachment && l.LimitAmount == layer.Limit, cancellationToken)
                .ConfigureAwait(false);
            if (!carried)
            {
                Add(path, "LAYER_NOT_IN_CONTRACT");
            }
        }

        return errors.Count == 0
            ? null
            : new DomainError(ErrorCode.For(ModuleCode.RI, "VALIDATION"), "A contract of the programme does not fit its layer or period.") { FieldErrors = errors };
    }

    /// <summary>Another programme of the same legal entity and line whose current version overlaps the period is refused (one programme per line and period).</summary>
    public static async Task<DomainError?> CheckOverlapAsync(
        ReinsuranceDbContext db, LegalEntityId legalEntity, string lineCode, BusinessDate periodStart, BusinessDate periodEnd, RiProgrammeId? except, CancellationToken cancellationToken)
    {
        var exceptId = except?.Value;
        var clash = await (
            from p in db.Programmes.AsNoTracking()
            join v in db.ProgrammeVersions.AsNoTracking() on new { p.ProgrammeId, VersionNo = p.CurrentVersionNo } equals new { v.ProgrammeId, v.VersionNo }
            where p.LegalEntityId == legalEntity && p.LineCode == lineCode && (exceptId == null || p.ProgrammeId != except)
                && v.Status != ProgrammeStatuses.Superseded && v.ValidFrom < periodEnd && periodStart < v.ValidTo
            select p.Code).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return clash is null
            ? null
            : RiErrors.Validation("period", "PROGRAMME_PERIOD_OVERLAP", $"The programme {clash} already covers part of this period for the line.");
    }

    /// <summary>True when a save lost a race on the named unique index.</summary>
    public static bool IsUnique(DbUpdateException exception, string constraint) => ContractSupport.IsUniqueViolation(exception, constraint);

    /// <summary>The wire view of <paramref name="programmes"/>, loaded in a few batched queries.</summary>
    public static async Task<IReadOnlyList<RiProgramme>> ViewsAsync(
        ReinsuranceDbContext db, string legalEntityCode, IReadOnlyList<ProgrammeRow> programmes, CancellationToken cancellationToken)
    {
        if (programmes.Count == 0)
        {
            return [];
        }

        var ids = programmes.Select(p => p.ProgrammeId).ToArray();
        var versions = await db.ProgrammeVersions.AsNoTracking().Where(v => ids.Contains(v.ProgrammeId)).OrderBy(v => v.VersionNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var versionIds = versions.Select(v => v.VersionId).ToArray();
        var layers = await db.ProgrammeLayers.AsNoTracking().Where(l => versionIds.Contains(l.VersionId)).OrderBy(l => l.LayerNo).ToListAsync(cancellationToken).ConfigureAwait(false);
        var contractIds = layers.Select(l => l.ContractId).Distinct().ToArray();
        var contracts = await db.Contracts.AsNoTracking().Where(c => contractIds.Contains(c.ContractId)).ToDictionaryAsync(c => c.ContractId, cancellationToken).ConfigureAwait(false);
        var views = new List<RiProgramme>();
        foreach (var programme in programmes)
        {
            var history = versions.Where(v => v.ProgrammeId == programme.ProgrammeId).ToList();
            var current = history.Single(v => v.VersionNo == programme.CurrentVersionNo);
            views.Add(ToView(programme, current, history, [.. layers.Where(l => l.VersionId == current.VersionId)], contracts, legalEntityCode));
        }

        return views;
    }

    private static RiProgramme ToView(
        ProgrammeRow programme, ProgrammeVersionRow current, IReadOnlyList<ProgrammeVersionRow> history, IReadOnlyList<ProgrammeLayerRow> layers,
        Dictionary<RiContractId, ContractRow> contracts, string legalEntityCode)
    {
        var eur = Currency.FromCode(programme.Currency.Trim());
        var ordered = layers.OrderBy(l => l.LayerNo).ToList();
        return new RiProgramme
        {
            ProgrammeId = programme.ProgrammeId,
            LegalEntity = legalEntityCode,
            Code = programme.Code,
            Name = current.Name,
            LineCode = programme.LineCode,
            ProgrammeYear = programme.ProgrammeYear,
            Currency = eur,
            Period = DateRange.Of(current.ValidFrom, current.ValidTo),
            Status = ProgrammeStatuses.Wire(current.Status),
            VersionNo = current.VersionNo,
            RecordVersion = programme.RecordVersion,
            Retention = new Money(ContractSupport.Amount(ordered.Count == 0 ? 0m : ordered[0].Attachment), eur),
            Layers =
            [
                .. ordered.Select(l => new RiProgrammeLayer
                {
                    LayerNo = l.LayerNo,
                    Attachment = new Money(ContractSupport.Amount(l.Attachment), eur),
                    Limit = new Money(ContractSupport.Amount(l.LimitAmount), eur),
                    ContractId = l.ContractId,
                    ContractNumber = contracts[l.ContractId].ContractNumber,
                    ContractStatus = ContractSupport.Wire(ContractStateModel.Parse(contracts[l.ContractId].Status)),
                }),
            ],
            Contracts =
            [
                .. ordered.Select(l => l.ContractId).Distinct().Select(id => contracts[id]).Select(c => new RiProgrammeContract
                {
                    ContractId = c.ContractId,
                    ContractNumber = c.ContractNumber,
                    ContractType = RiContractType.XolPerRisk,
                    ContractYear = c.ContractYear,
                    Status = ContractSupport.Wire(ContractStateModel.Parse(c.Status)),
                    Currency = Currency.FromCode(c.Currency.Trim()),
                }),
            ],
            RenewedFromId = programme.RenewedFromId?.Value,
            Versions =
            [
                .. history.Select(v => new RiProgrammeVersionSummary
                {
                    VersionNo = v.VersionNo, Status = ProgrammeStatuses.Wire(v.Status), Period = DateRange.Of(v.ValidFrom, v.ValidTo), CreatedAt = v.CreatedAt, CreatedBy = v.CreatedBy,
                }),
            ],
            CreatedAt = programme.CreatedAt,
        };
    }
}

/// <summary>Read side of <c>ri.Programme.list / get</c>: scoped to the caller's legal entity, keyset paging newest programme year first.</summary>
internal sealed class ProgrammeReader(ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>One programme, or null when it is not in the caller's legal entity.</summary>
    public async Task<RiProgramme?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var (entity, code) = Entity();
        var programme = await db.Programmes.AsNoTracking().SingleOrDefaultAsync(p => p.ProgrammeId == new RiProgrammeId(id) && p.LegalEntityId == entity, cancellationToken).ConfigureAwait(false);
        return programme is null ? null : (await ProgrammeSupport.ViewsAsync(db, code, [programme], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>A page: the cursor is <c>year.code</c> of the last item; sorted by programme year descending, then code.</summary>
    public async Task<Result<(IReadOnlyList<ProgrammeListItem> Items, string? Next, int Limit)>> ListAsync(string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            return RiErrors.Validation("limit", "LIMIT_RANGE", $"The limit is between 1 and {MaxLimit}.");
        }

        var beforeYear = int.MaxValue;
        var afterCode = string.Empty;
        if (cursor is not null)
        {
            var parts = cursor.Split('.', 2);
            afterCode = parts.Length == 2 ? parts[1] : string.Empty;
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out beforeYear) || !ProgrammeRules.IsCode(afterCode))
            {
                return RiErrors.Validation("cursor", "CURSOR", "The cursor is not valid.");
            }
        }

        var (entity, code) = Entity();
        var query = db.Programmes.AsNoTracking().Where(p => p.LegalEntityId == entity);
        if (cursor is not null)
        {
            query = query.Where(p => p.ProgrammeYear < beforeYear || (p.ProgrammeYear == beforeYear && p.Code.CompareTo(afterCode) > 0));
        }

        var rows = await query.OrderByDescending(p => p.ProgrammeYear).ThenBy(p => p.Code).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        var views = await ProgrammeSupport.ViewsAsync(db, code, page, cancellationToken).ConfigureAwait(false);
        var next = rows.Count > size ? FormattableString.Invariant($"{page[^1].ProgrammeYear}.{page[^1].Code}") : null;
        return ((IReadOnlyList<ProgrammeListItem>)[.. views.Select(v => new ProgrammeListItem { Programme = v })], next, size);
    }

    private (LegalEntityId Id, string Code) Entity()
    {
        var code = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        return (legalEntities.Resolve(code), code.Value);
    }
}
