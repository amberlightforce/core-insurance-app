using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Persistence;
using CoreIns.Modules.Data.Services.Marts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Data.Services.Coverage;

/// <summary>
/// Control totals of the reporting layers (BO-DAT-05, replaces CDC per D-ARC-06). Read-only. For every raw event accepted in a period (by received
/// time) it says whether the event was projected into a conformed fact, is quarantined, is in a gap (a handled type that is neither projected nor
/// quarantined: a dropped projection) or is of a type nothing consumes. The totals are shown per conformed fact table and per mart.
/// TODO(hardening): producer-side control totals (events written by each module's outbox) cannot be read across modules yet, so an event that never
/// reached the ledger is invisible; compare against a producer count feed when one exists.
/// </summary>
internal sealed class CoverageReader(DataDbContext db, IClock clock)
{
    /// <summary>Registry name -> conformed fact table that the projector writes.</summary>
    internal static readonly IReadOnlyDictionary<string, string> TableOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pol.PolicyIssued"] = "fact_policy_term",
        ["pol.PolicyChanged"] = "fact_policy_term",
        ["pol.PolicyCancelled"] = "fact_policy_term",
        ["pol.PolicyReinstated"] = "fact_policy_term",
        ["pol.PolicyLapsed"] = "fact_policy_term",
        ["bil.InvoiceIssued"] = "fact_premium",
        ["clm.ReserveChanged"] = "fact_claim_movement",
        ["clm.PaymentIssued"] = "fact_claim_movement",
        ["clm.PaymentVoided"] = "fact_claim_movement",
    };

    /// <summary>The event type RI_CESSION_SUMMARY reads straight from the raw ledger (there is no conformed RI fact).</summary>
    internal const string RiRegistry = "ri.CessionCalculated";

    /// <summary>Mart code -> the registry names its figures come from.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> MartSources = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [PortfolioPremiumMart.MartCode] = ["bil.InvoiceIssued"],
        [ClaimsExperienceMart.MartCode] = ["bil.InvoiceIssued", "clm.ReserveChanged", "clm.PaymentIssued", "clm.PaymentVoided"],
        [RiCessionSummaryMart.MartCode] = [RiRegistry],
    };

    private sealed class RawLine
    {
        public string Registry { get; set; } = string.Empty;

        public string Period { get; set; } = string.Empty;

        public long Received { get; set; }

        public long Projected { get; set; }

        public long Quarantined { get; set; }

        public long Open { get; set; }
    }

    public async Task<Result<CoverageStatusResponse>> StatusAsync(string legalEntity, string? period, CancellationToken ct)
    {
        if (!AnalyticsReader.TryPeriod(period, out var from, out var to))
        {
            return DomainError.Of(ModuleCode.DAT, PlatformErrors.Validation, "period must be 'YYYY', 'YYYY-MM' or 'from..to'.");
        }

        var raw = await ReadAsync(legalEntity, from, to, ct).ConfigureAwait(false);
        var tables = new List<CoverageLine>();
        var notHandled = new List<CoverageLine>();
        foreach (var group in raw.Where(r => TableOf.ContainsKey(r.Registry)).GroupBy(r => (Table: TableOf[r.Registry], r.Period)).OrderBy(g => g.Key.Table, StringComparer.Ordinal).ThenBy(g => g.Key.Period, StringComparer.Ordinal))
        {
            tables.Add(Line(group.Key.Table, group.Key.Period, group.Sum(r => r.Received), group.Sum(r => r.Projected), group.Sum(r => r.Quarantined), group.Sum(r => r.Open), 0));
        }

        foreach (var r in raw.Where(r => !TableOf.ContainsKey(r.Registry) && r.Registry != RiRegistry).OrderBy(r => r.Registry, StringComparer.Ordinal).ThenBy(r => r.Period, StringComparer.Ordinal))
        {
            notHandled.Add(Line(r.Registry, r.Period, r.Received, 0, r.Quarantined, 0, r.Received - r.Quarantined));
        }

        var marts = new List<CoverageLine>();
        foreach (var (mart, sources) in MartSources.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            foreach (var group in raw.Where(r => sources.Contains(r.Registry, StringComparer.Ordinal)).GroupBy(r => r.Period).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                // The RI mart reads the ledger directly: an accepted, unquarantined event is "projected" for it.
                var direct = mart == RiCessionSummaryMart.MartCode;
                marts.Add(Line(
                    mart, group.Key, group.Sum(r => r.Received), group.Sum(r => direct ? r.Projected + r.Open : r.Projected), group.Sum(r => r.Quarantined), direct ? 0 : group.Sum(r => r.Open), 0));
            }
        }

        return new CoverageStatusResponse
        {
            Period = period,
            GeneratedAt = AnalyticsReader.Iso(clock.Now.ToUtcDateTime()),
            GapCount = (int)tables.Sum(t => t.Gap),
            Tables = tables,
            Marts = marts,
            NotHandled = notHandled,
        };
    }

    private static CoverageLine Line(string name, string period, long received, long projected, long quarantined, long gap, long notHandled) => new()
    {
        Name = name, Period = period, Received = (int)received, Projected = (int)projected, Quarantined = (int)quarantined, Gap = (int)gap, NotHandled = (int)notHandled,
    };

    private async Task<List<RawLine>> ReadAsync(string legalEntity, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var values = new List<object> { legalEntity };
        var ledgerRange = string.Empty;
        var quarantineRange = string.Empty;
        if (from is { } f)
        {
            ledgerRange += $" AND l.received_at >= {{{values.Count}}}";
            quarantineRange += $" AND q.received_at >= {{{values.Count}}}";
            values.Add(f);
        }

        if (to is { } t)
        {
            ledgerRange += $" AND l.received_at < {{{values.Count}}}";
            quarantineRange += $" AND q.received_at < {{{values.Count}}}";
            values.Add(t);
        }

        var sql = $$"""
            WITH ev AS (
                SELECT l.registry_name AS registry, to_char(l.received_at AT TIME ZONE 'UTC', 'YYYY-MM') AS period,
                       (p.event_id IS NOT NULL) AS projected, (q.event_id IS NOT NULL) AS quarantined
                FROM rpt_raw.event_ledger l
                LEFT JOIN rpt_conformed.projected_event p ON p.event_id = l.event_id
                LEFT JOIN rpt_raw.quarantine q ON q.event_id = l.event_id AND q.status = 'QUARANTINED'
                WHERE l.legal_entity = {0}{{ledgerRange}}
                UNION ALL
                SELECT q.registry_name, to_char(q.received_at AT TIME ZONE 'UTC', 'YYYY-MM'), false, true
                FROM rpt_raw.quarantine q
                WHERE q.status = 'QUARANTINED'
                  AND (q.envelope->>'legalEntity' = {0} OR q.envelope->>'legalEntity' IS NULL)
                  AND NOT EXISTS (SELECT 1 FROM rpt_raw.event_ledger l2 WHERE l2.event_id = q.event_id){{quarantineRange}}
            )
            SELECT registry AS "Registry", period AS "Period", COUNT(*)::bigint AS "Received",
                   COUNT(*) FILTER (WHERE projected)::bigint AS "Projected",
                   COUNT(*) FILTER (WHERE NOT projected AND quarantined)::bigint AS "Quarantined",
                   COUNT(*) FILTER (WHERE NOT projected AND NOT quarantined)::bigint AS "Open"
            FROM ev
            GROUP BY 1, 2
            """;
        return await db.Database.SqlQueryRaw<RawLine>(sql, [.. values]).ToListAsync(ct).ConfigureAwait(false);
    }
}
