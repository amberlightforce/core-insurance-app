using System.Globalization;
using System.Text;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Queries;

/// <summary>
/// Claim tracking (BO-CLM-08, REQ-CLM-009): the watch list and single view of claims, derived from the claim row and the existing
/// financial balances (<see cref="FinancialsReader"/>); no state of its own. Oldest claim first, keyset cursor (created_at, claim_id).
/// TODO(hardening): financials are read claim by claim; a set-based balance query would remove the N+1 on large pages.
/// </summary>
internal sealed class ClaimTrackingReader(DbSession session, FinancialsReader financials, IClock clock, IOptions<ClaimsOptions> options)
{
    private const int MaxBatch = 200;

    private const string Columns =
        """
        c.claim_id AS ClaimId, c.claim_number AS ClaimNumber, c.policy_number AS PolicyNumber, c.insured_party_id AS InsuredPartyId,
        c.status AS Status, c.sub_status AS SubStatus, c.milestone AS Milestone, c.milestone_at AS MilestoneAt,
        c.loss_date::text AS LossDate, c.notice_on::text AS NoticeOn, c.created_at AS CreatedAt, c.closed_at AS ClosedAt,
        c.updated_at AS UpdatedAt, c.handler AS Handler, c.cat_event_code AS CatEventCode
        """;

    /// <summary>One claim by id or claim number; null when not found.</summary>
    public async Task<ClaimTrackingView?> GetAsync(LegalEntityId legalEntity, string idOrNumber, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.TryParse(idOrNumber, out var guid) ? guid : (Guid?)null;
        var record = await connection.QueryFirstOrDefaultAsync<TrackingRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM clm.claim c WHERE c.legal_entity_id = @le AND (c.claim_id = @id OR c.claim_number = @number) ORDER BY (c.claim_id = @id) DESC LIMIT 1",
            new { le = legalEntity.Value, id, number = idOrNumber }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return record is null ? null : await ViewAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The watch list, one page: open claims by default, filters AND-combined; the large-loss filter reads on until the page is full.</summary>
    public async Task<ClaimTrackingListPage> ListAsync(
        LegalEntityId legalEntity, TrackingFilter filter, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var (afterAt, afterId) = DecodeCursor(cursor);
        var items = new List<ClaimTrackingView>();
        TrackingRecord? lastIncluded = null;
        var hasMore = false;
        var maxAge = filter.MinAgeDays is { } days ? clock.Now.Minus(TimeSpan.FromDays(days)).ToUtcDateTime() : (DateTime?)null;
        var batchSize = filter.LargeLoss == true ? MaxBatch : limit + 1;
        while (!hasMore)
        {
            var batch = (await connection.QueryAsync<TrackingRecord>(new CommandDefinition(
                $"""
                 SELECT {Columns}
                   FROM clm.claim c
                  WHERE c.legal_entity_id = @le
                    AND c.status = @status
                    AND (@claim::text IS NULL OR c.claim_number = @claim OR c.claim_id::text = @claim)
                    AND (@party::uuid IS NULL OR c.insured_party_id = @party)
                    AND (@policy::text IS NULL OR c.policy_number = @policy)
                    AND (@cat::text IS NULL OR c.cat_event_code = @cat)
                    AND (@maxAge::timestamptz IS NULL OR c.created_at <= @maxAge)
                    AND (@afterAt::timestamptz IS NULL OR (c.created_at, c.claim_id) > (@afterAt::timestamptz, @afterId::uuid))
                  ORDER BY c.created_at, c.claim_id
                  LIMIT @take
                 """,
                new
                {
                    le = legalEntity.Value, status = filter.Status, claim = filter.Claim, party = Guid.TryParse(filter.Party, out var partyId) ? partyId : (Guid?)null, policy = filter.Policy, cat = filter.CatCode,
                    maxAge, afterAt, afterId, take = batchSize,
                },
                session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
            foreach (var record in batch)
            {
                var view = await ViewAsync(record, cancellationToken).ConfigureAwait(false);
                afterAt = record.CreatedAt;
                afterId = record.ClaimId;
                if (filter.LargeLoss == true && !view.LargeLoss)
                {
                    continue;
                }

                if (items.Count == limit)
                {
                    hasMore = true;
                    break;
                }

                items.Add(view);
                lastIncluded = record;
            }

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return new ClaimTrackingListPage
        {
            Items = [.. items.Select(v => new ClaimTrackingListItem { TrackingView = v })],
            NextCursor = hasMore && lastIncluded is not null ? EncodeCursor(lastIncluded) : null,
            Limit = limit,
        };
    }

    private async Task<ClaimTrackingView> ViewAsync(TrackingRecord r, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var balances = (await financials.GetAsync(new ClaimId(r.ClaimId), now, cancellationToken).ConfigureAwait(false)).Totals;
        var createdAt = Instant.FromUtcDateTime(r.CreatedAt);
        var until = r.ClosedAt is { } closed ? Instant.FromUtcDateTime(closed) : now;
        return new ClaimTrackingView
        {
            ClaimId = new ClaimId(r.ClaimId),
            ClaimNumber = ClaimNumber.Parse(r.ClaimNumber),
            PolicyNumber = PolicyNumber.Parse(r.PolicyNumber),
            InsuredPartyId = new PartyId(r.InsuredPartyId),
            Status = r.Status,
            SubStatus = r.SubStatus,
            Milestone = r.Milestone,
            MilestoneAt = r.MilestoneAt is { } at ? Instant.FromUtcDateTime(at) : null,
            LossDate = r.LossDate,
            NoticeOn = r.NoticeOn,
            OpenedAt = createdAt,
            AgeDays = Math.Max(0, until.Since(createdAt).Days),
            UnreviewedDays = r.Status == ClaimStates.Open ? Math.Max(0, now.Since(Instant.FromUtcDateTime(r.UpdatedAt)).Days) : null,
            LargeLoss = balances.Incurred.Amount >= options.Value.Tracking.LargeLossThreshold,
            Handler = r.Handler,
            CatEventCode = r.CatEventCode,
            OpenReserve = balances.OpenReserve,
            Paid = balances.Paid,
            Incurred = balances.Incurred,
        };
    }

    private static string EncodeCursor(TrackingRecord r) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{r.CreatedAt.Ticks}|{r.ClaimId:D}"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static (DateTime? At, Guid? Id) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null);
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            return (new DateTime(long.Parse(parts[0], CultureInfo.InvariantCulture), DateTimeKind.Utc), Guid.Parse(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new FormatException("The cursor is not valid.", ex);
        }
    }

    public static bool TryPage(int? limit, out int take)
    {
        take = limit ?? 50;
        return take is >= 1 and <= 200;
    }

#pragma warning disable CA1812 // Instantiated by Dapper.
    internal sealed class TrackingRecord
    {
        public Guid ClaimId { get; set; }

        public string ClaimNumber { get; set; } = string.Empty;

        public string PolicyNumber { get; set; } = string.Empty;

        public Guid InsuredPartyId { get; set; }

        public string Status { get; set; } = string.Empty;

        public string? SubStatus { get; set; }

        public string? Milestone { get; set; }

        public DateTime? MilestoneAt { get; set; }

        public string LossDate { get; set; } = string.Empty;

        public string NoticeOn { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public string? Handler { get; set; }

        public string? CatEventCode { get; set; }
    }
#pragma warning restore CA1812
}

/// <summary>Filters of the watch list.</summary>
internal sealed record TrackingFilter(string Status, string? Claim, string? Party, string? Policy, string? CatCode, bool? LargeLoss, int? MinAgeDays);

/// <summary>The CAT event register and its aggregates (BO-CLM-08): reads only; money comes from <see cref="FinancialsReader"/>.</summary>
internal sealed class CatEventReader(ClaimsDbContext db, DbSession session, FinancialsReader financials, IClock clock, IOptions<ClaimsOptions> options)
{
    public async Task<CatEventListResponse> ListAsync(LegalEntityId legalEntity, string? status, CancellationToken cancellationToken)
    {
        var query = db.CatEvents.AsNoTracking().Where(e => e.LegalEntityId == legalEntity);
        if (status is not null)
        {
            query = query.Where(e => e.Status == status);
        }

        var rows = await query.OrderByDescending(e => e.WindowFrom).ThenBy(e => e.CatCode).ToListAsync(cancellationToken).ConfigureAwait(false);
        var counts = await db.Claims.AsNoTracking().Where(c => c.LegalEntityId == legalEntity && c.CatEventCode != null)
            .GroupBy(c => c.CatEventCode!).Select(g => new { Code = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Code, g => g.Count, cancellationToken).ConfigureAwait(false);
        return new CatEventListResponse { Events = [.. rows.Select(r => Commands.CatEventSupport.View(r, counts.GetValueOrDefault(r.CatCode)))] };
    }

    /// <summary>
    /// Totals by currency and line of business of the claims tagged to the event(s), from the approved claim financials as of
    /// <paramref name="validAt"/> (an instant bounds the record time; a date means the end of that business day; null = now).
    /// </summary>
    public async Task<CatEventAggregateResponse> AggregateAsync(LegalEntityId legalEntity, string? catCode, ValidAt? validAt, CancellationToken cancellationToken)
    {
        var knownAt = validAt switch
        {
            { Instant: { } instant } => instant,
            { Date: { } date } => date.AddDays(1).StartOfDayIn(options.Value.Zone),
            _ => clock.Now,
        };
        var events = await db.CatEvents.AsNoTracking().Where(e => e.LegalEntityId == legalEntity && (catCode == null || e.CatCode == catCode))
            .OrderBy(e => e.CatCode).ToListAsync(cancellationToken).ConfigureAwait(false);
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CatEventAggregate>();
        foreach (var cat in events)
        {
            var claims = (await connection.QueryAsync<(Guid ClaimId, string Lob)>(new CommandDefinition(
                "SELECT c.claim_id, c.line_of_business FROM clm.claim c WHERE c.legal_entity_id = @le AND c.cat_event_code = @cat ORDER BY c.claim_number",
                new { le = legalEntity.Value, cat = cat.CatCode }, session.Transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
            var rows = new List<(string Lob, FinancialBalance Balance)>();
            foreach (var claim in claims)
            {
                rows.Add((claim.Lob, (await financials.GetAsync(new ClaimId(claim.ClaimId), knownAt, cancellationToken).ConfigureAwait(false)).Totals));
            }

            result.Add(new CatEventAggregate
            {
                CatCode = cat.CatCode,
                Name = cat.Name,
                Status = cat.Status == CatEventRow.Open ? CatEventAggregate.StatusValue.Open : CatEventAggregate.StatusValue.Closed,
                ClaimCount = claims.Count,
                TotalsByCurrency = Sum(rows.Select(r => r.Balance)),
                TotalsByLine = [.. rows.GroupBy(r => r.Lob).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new CatEventLineTotals { LineOfBusiness = g.Key, Totals = Sum(g.Select(r => r.Balance)) })],
            });
        }

        return new CatEventAggregateResponse { Events = result };
    }

    private static List<CatEventTotals> Sum(IEnumerable<FinancialBalance> balances) =>
        [.. balances.GroupBy(b => b.Incurred.Currency).OrderBy(g => g.Key.Code, StringComparer.Ordinal).Select(g => new CatEventTotals
        {
            ClaimCount = g.Count(),
            Reserved = new Money(g.Sum(b => b.Reserved.Amount), g.Key),
            Paid = new Money(g.Sum(b => b.Paid.Amount), g.Key),
            OpenReserve = new Money(g.Sum(b => b.OpenReserve.Amount), g.Key),
            Incurred = new Money(g.Sum(b => b.Incurred.Amount), g.Key),
        })];
}
