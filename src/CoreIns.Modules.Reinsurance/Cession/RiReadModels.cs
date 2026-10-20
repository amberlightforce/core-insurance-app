using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Context;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Cession;

/// <summary>
/// Read side of cessions, cession exceptions, programmes and participations (<c>ri.Cession.*</c>, <c>ri.CessionException.list</c>,
/// <c>ri.Programme.list/get</c>, <c>ri.Participation.list</c>; contracts/openapi/ri.yaml). Everything is scoped to the caller's legal
/// entity (another entity's row is "not found"). Pages are keyset pages: the cursor is the last key of the previous page, the
/// sort is the key (stable under inserts), the page size is capped.
/// </summary>
internal sealed class RiReadModels(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    private LegalEntityId LegalEntityKey => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private Guid LegalEntity => LegalEntityKey.Value;

    /// <summary><c>ri.Cession.listByPolicy</c>: the cession rows of the policy's terms (terms known from the premium deltas).</summary>
    public async Task<Result<(IReadOnlyList<CessionListByPolicyItem> Items, string? Next, int Limit)>> ListByPolicyAsync(
        Guid policyId, Guid? termId, Instant? knownAt, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (policyId == Guid.Empty)
        {
            return RiErrors.Validation("policy", "POLICY_REQUIRED", "A policy id is required.");
        }

        var paging = Paging(knownAt, cursor, limit, out var after, out var size);
        if (paging is not null)
        {
            return paging;
        }

        var legalEntity = LegalEntity;
        var terms = db.PremiumDeltas.AsNoTracking().Where(p => p.LegalEntityId == legalEntity && p.PolicyId == policyId).Select(p => p.TermId);
        var query = db.Cessions.AsNoTracking().Where(c => c.LegalEntityId == legalEntity && terms.Contains(c.TermId));
        if (termId is { } term)
        {
            query = query.Where(c => c.TermId == term);
        }

        if (knownAt is { } cutoff)
        {
            query = query.Where(c => c.RecordedAt <= cutoff);
        }

        if (after is { } last)
        {
            query = query.Where(c => c.CessionId.CompareTo(last) > 0);
        }

        var rows = await query.OrderBy(c => c.CessionId).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (rows.Take(size).Select(ToItem).ToList(), rows.Count > size ? rows[size - 1].CessionId.ToString("D") : null, size);
    }

    /// <summary><c>ri.Cession.get</c>: null when the cession is not in the caller's legal entity.</summary>
    public async Task<CessionGetResponse?> GetCessionAsync(Guid id, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var row = await db.Cessions.AsNoTracking().SingleOrDefaultAsync(c => c.CessionId == id && c.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var item = ToItem(row);
        return new CessionGetResponse { Intervals = item.Intervals, Shares = item.Shares, Deltas = item.Deltas };
    }

    /// <summary><c>ri.CessionException.list</c>, by key.</summary>
    public async Task<Result<(IReadOnlyList<CessionExceptionListItem> Items, string? Next, int Limit)>> ListExceptionsAsync(
        string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var paging = Paging(null, cursor, limit, out var after, out var size);
        if (paging is not null)
        {
            return paging;
        }

        var legalEntity = LegalEntity;
        var query = db.CessionExceptions.AsNoTracking().Where(e => e.LegalEntityId == legalEntity);
        if (after is { } last)
        {
            query = query.Where(e => e.ExceptionId.CompareTo(last) > 0);
        }

        var rows = await query.OrderBy(e => e.ExceptionId).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = rows.Take(size).Select(e => new CessionExceptionListItem
        {
            Exception = Json(new
            {
                exceptionId = e.ExceptionId, termId = e.TermId, sourceEventId = e.SourceEventId, code = e.Code, detail = e.Detail, recordedAt = e.RecordedAt.ToString(),
            }),
            Deltas = Json(Array.Empty<object>()), // TODO(hardening): link the cession deltas a rerun of this exception produced (rerun/resolve are not built).
        }).ToList();
        return (items, rows.Count > size ? rows[size - 1].ExceptionId.ToString("D") : null, size);
    }

    /// <summary>
    /// <c>ri.Participation.list</c>: the signed lines of the current content of each current contract version, with the sum check.
    /// <paramref name="contractId"/> narrows to one contract.
    /// </summary>
    public async Task<Result<(IReadOnlyList<ParticipationListItem> Items, string? Next, int Limit)>> ListParticipationsAsync(
        Guid? contractId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var paging = Paging(null, cursor, limit, out var after, out var size);
        if (paging is not null)
        {
            return paging;
        }

        var legalEntity = LegalEntityKey;
        var query =
            from p in db.Participations.AsNoTracking()
            join v in db.Versions.AsNoTracking() on p.VersionId equals v.VersionId
            join c in db.Contracts.AsNoTracking() on v.ContractId equals c.ContractId
            where c.LegalEntityId == legalEntity && v.KnownTo == null && p.Rev == v.ContentRev
            select new { p, v, c };
        if (contractId is { } wanted)
        {
            var wantedId = new RiContractId(wanted);
            query = query.Where(x => x.c.ContractId == wantedId);
        }

        if (after is { } last)
        {
            query = query.Where(x => x.p.ParticipationId.CompareTo(last) > 0);
        }

        var rows = await query.OrderBy(x => x.p.ParticipationId).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(size).ToList();
        var versionIds = page.Select(x => x.v.VersionId).Distinct().ToArray();
        var current = page.Select(x => x.v).DistinctBy(v => v.VersionId).ToDictionary(v => v.VersionId, v => v.ContentRev);
        var sums = (await db.Participations.AsNoTracking().Where(p => versionIds.Contains(p.VersionId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(p => p.Rev == current[p.VersionId])
            .GroupBy(p => p.VersionId).ToDictionary(g => g.Key, g => g.Sum(p => p.SignedLinePct));
        var items = page.Select(x => new ParticipationListItem
        {
            Participations = Json(new
            {
                participationId = x.p.ParticipationId, contractId = x.c.ContractId.Value, contractNumber = x.c.ContractNumber, contractYear = x.c.ContractYear,
                reinsurerPartyId = x.p.ReinsurerPartyId.Value, brokerPartyId = x.p.BrokerPartyId, signedLinePct = Pct(x.p.SignedLinePct), lead = x.p.Lead,
            }),
            Checks = Json(new { signedLinesSumPct = Pct(sums[x.v.VersionId]), placedPct = Pct(x.v.PlacedPct), signedLinesMatchPlaced = sums[x.v.VersionId] == x.v.PlacedPct }),
        }).ToList();
        return (items, rows.Count > size ? rows[size - 1].p.ParticipationId.ToString("D") : null, size);
    }

    /// <summary>
    /// <c>ri.Programme.list</c>: a programme is the set of a legal entity's contracts of one contract year, newest year first.
    /// TODO(hardening): there is no programme aggregate yet (create/update/renew are not built); this is a read projection.
    /// </summary>
    public async Task<Result<(IReadOnlyList<ProgrammeListItem> Items, string? Next, int Limit)>> ListProgrammesAsync(
        string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            return RiErrors.Validation("limit", "LIMIT_RANGE", $"The limit is between 1 and {MaxLimit}.");
        }

        var before = int.MaxValue;
        if (cursor is not null && !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out before))
        {
            return RiErrors.Validation("cursor", "CURSOR", "The cursor is not valid.");
        }

        var legalEntity = LegalEntityKey;
        var years = await db.Contracts.AsNoTracking().Where(c => c.LegalEntityId == legalEntity && c.ContractYear < before)
            .Select(c => c.ContractYear).Distinct().OrderByDescending(y => y).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = years.Take(size).ToList();
        var items = new List<ProgrammeListItem>();
        foreach (var year in page)
        {
            items.Add(new ProgrammeListItem { Programmes = await ProgrammeAsync(legalEntity, year, cancellationToken).ConfigureAwait(false) });
        }

        return (items, years.Count > size ? page[^1].ToString(CultureInfo.InvariantCulture) : null, size);
    }

    /// <summary><c>ri.Programme.get</c>: the programme of one contract year; null when the entity has no contract of that year.</summary>
    public async Task<ProgrammeGetResponse?> GetProgrammeAsync(string id, CancellationToken cancellationToken)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }

        var element = await ProgrammeAsync(LegalEntityKey, year, cancellationToken).ConfigureAwait(false);
        return element is null ? null : new ProgrammeGetResponse { Programmes = element };
    }

    private async Task<JsonElement?> ProgrammeAsync(LegalEntityId legalEntity, int year, CancellationToken cancellationToken)
    {
        var contracts = await db.Contracts.AsNoTracking().Where(c => c.LegalEntityId == legalEntity && c.ContractYear == year)
            .OrderBy(c => c.ContractNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (contracts.Count == 0)
        {
            return null;
        }

        return Json(new
        {
            programmeId = year.ToString(CultureInfo.InvariantCulture),
            contractYear = year,
            contracts = contracts.Select(c => new { contractId = c.ContractId.Value, contractNumber = c.ContractNumber, contractType = c.ContractType, status = c.Status, currency = c.Currency.Trim() }),
        });
    }

    private DomainError? Paging(Instant? knownAt, string? cursor, int? limit, out Guid? after, out int size)
    {
        after = null;
        size = limit ?? DefaultLimit;
        if (knownAt is { } at && at > clock.Now)
        {
            return RiErrors.Validation("knownAt", "KNOWN_AT_FUTURE", "knownAt must not be in the future.");
        }

        if (size is < 1 or > MaxLimit)
        {
            return RiErrors.Validation("limit", "LIMIT_RANGE", $"The limit is between 1 and {MaxLimit}.");
        }

        if (cursor is not null)
        {
            if (!Guid.TryParseExact(cursor, "D", out var parsed))
            {
                return RiErrors.Validation("cursor", "CURSOR", "The cursor is not valid.");
            }

            after = parsed;
        }

        return null;
    }

    private static CessionListByPolicyItem ToItem(CessionRow c) => new()
    {
        Intervals = Json(new { cessionId = c.CessionId, termId = c.TermId, contractYear = c.ContractYear, setKey = c.SetKey }),
        Shares = Json(new { treatyId = c.TreatyId, mode = c.Mode, writtenPremium = Money(c.WrittenPremium), target = Money(c.Target) }),
        Deltas = Json(new { bookedBefore = Money(c.BookedBefore), delta = Money(c.Delta), recordedAt = c.RecordedAt.ToString() }),
    };

    private static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Pct(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
}
