using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>
/// Reads the IPT (LA-06) and levy (LA-07) payable accruals of the sub-ledger per accounting month and currency (bil.TaxLevy.periods / detail,
/// REQ-BIL-269..278). Accrued = credits minus debits on the payable account. Reads only: tax payments are TAX_REMITTANCE disbursements requested by FIN.
/// Levy tracking (BO-BIL-14): the levy payable per levy authority (the charge type on the ledger line) and month, with the payable
/// requested from it and the state of its payment (requested, approved, paid), all exact sums of stored amounts.
/// TODO(hardening): levy lines are read into memory per request; aggregate in SQL and index (account_code, charge_type) when volumes grow.
/// </summary>
internal sealed class TaxLevyReader(BillingDbContext db)
{
    private static readonly string[] PayableAccounts = ["LA-06", "LA-07"];

    private sealed record Bucket(string Period, string Kind, string Currency, decimal Accrued, int Lines);

    /// <summary>Per-period totals; <paramref name="period"/> (yyyy-MM) narrows to one month.</summary>
    public async Task<TaxLevyPeriodsResponse> PeriodsAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var buckets = await BucketsAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        var totals = buckets.GroupBy(b => (b.Kind, b.Currency))
            .Select(g => new { kind = g.Key.Kind, currency = g.Key.Currency, accrued = g.Sum(b => b.Accrued) }).ToList();
        var detail = buckets.GroupBy(b => b.Period).OrderBy(g => g.Key)
            .Select(g => new { period = g.Key, items = g.Select(b => new { kind = b.Kind, currency = b.Currency, accrued = b.Accrued }).ToList() }).ToList();
        return new TaxLevyPeriodsResponse { Totals = JsonSerializer.SerializeToElement(totals, JsonSerializerOptions.Web), Detail = JsonSerializer.SerializeToElement(detail, JsonSerializerOptions.Web) };
    }

    /// <summary>Per-period, per-rule detail of the accruals; <paramref name="period"/> (yyyy-MM) narrows to one month.</summary>
    public async Task<TaxLevyDetailResponse> DetailAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var rows = await LinesAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        var detail = rows.GroupBy(r => (r.Period, r.AccountCode, r.RuleId, r.Currency)).OrderBy(g => g.Key.Period).ThenBy(g => g.Key.AccountCode).ThenBy(g => g.Key.RuleId)
            .Select(g => new
            {
                period = g.Key.Period,
                kind = Kind(g.Key.AccountCode),
                ruleId = g.Key.RuleId,
                currency = g.Key.Currency,
                accrued = g.Sum(r => Signed(r.Side, r.Amount)),
                lines = g.Count(),
            }).ToList();
        var totals = rows.GroupBy(r => (r.AccountCode, r.Currency))
            .Select(g => new { kind = Kind(g.Key.AccountCode), currency = g.Key.Currency, accrued = g.Sum(r => Signed(r.Side, r.Amount)) }).ToList();
        return new TaxLevyDetailResponse
        {
            Totals = JsonSerializer.SerializeToElement(totals, JsonSerializerOptions.Web),
            Detail = JsonSerializer.SerializeToElement(detail, JsonSerializerOptions.Web),
            Levies = await TrackingAsync(legalEntity, period, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// The levy tracking per levy authority, month and currency: collected (net levy lines), the payable created from it, and how much of
    /// that payable is requested, approved or paid; outstanding is collected less the payable (all of it when the payment is dead).
    /// </summary>
    public async Task<List<TaxLevyTrackingItem>> TrackingAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var buckets = await LevyBucketsAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        var payables = await db.LevyPayables.AsNoTracking().Where(p => p.LegalEntityId == legalEntity && (period == null || p.Period == period)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = payables.Select(p => p.DisbursementId).Distinct().ToList();
        var states = await db.Disbursements.AsNoTracking().Where(d => ids.Contains(d.DisbursementId)).Select(d => new { d.DisbursementId, d.State }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var stateOf = states.ToDictionary(d => d.DisbursementId, d => d.State);

        var items = new List<TaxLevyTrackingItem>();
        foreach (var bucket in buckets)
        {
            var currency = Currency.FromCode(bucket.Currency);
            var payable = payables.FirstOrDefault(p => p.Period == bucket.Period && p.LevyAuthority == bucket.LevyType && p.Currency == bucket.Currency);
            var state = payable is not null && stateOf.TryGetValue(payable.DisbursementId, out var found) ? found : null;
            var amount = payable is null ? Money.Zero(currency) : new Money(payable.PayableAmount, currency);
            var collected = new Money(bucket.Net, currency);
            var group = state is null ? LevyPaymentState.None : LevyPaymentStates.Of(state);
            items.Add(new TaxLevyTrackingItem
            {
                LevyType = bucket.LevyType,
                Period = bucket.Period,
                Currency = bucket.Currency,
                Collected = collected,
                Payable = amount,
                Requested = group == LevyPaymentState.Requested ? amount : Money.Zero(currency),
                Approved = group == LevyPaymentState.Approved ? amount : Money.Zero(currency),
                Paid = group == LevyPaymentState.Paid ? amount : Money.Zero(currency),
                Outstanding = group == LevyPaymentState.Dead ? collected : collected - amount,
                LegalStatus = bucket.LegalStatus,
                Provisional = bucket.LegalStatus != LevyLegal.Settled,
                PayableId = payable?.LevyPayableId,
                DisbursementId = payable?.DisbursementId,
                DisbursementStatus = state,
            });
        }

        return items;
    }

    /// <summary>The net levy (credits minus debits) of one levy authority in one month and currency, with the weakest legal status behind it; null when it has no lines.</summary>
    public async Task<LevyBucket?> LevyBasisAsync(LegalEntityId legalEntity, string levyType, string period, string currency, CancellationToken cancellationToken) =>
        (await LevyBucketsAsync(legalEntity, period, cancellationToken).ConfigureAwait(false)).SingleOrDefault(b => b.LevyType == levyType && b.Currency == currency);

    private async Task<List<LevyBucket>> LevyBucketsAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var rows = (await LinesAsync(legalEntity, period, cancellationToken).ConfigureAwait(false))
            .Where(r => r.AccountCode == "LA-07" && r.SourceType != DisbursementCodes.LevyPayment).ToList();
        var chargeIds = rows.Where(r => r.ChargeId is not null).Select(r => r.ChargeId!.Value).Distinct().ToList();
        var charges = await db.Charges.AsNoTracking().Where(c => chargeIds.Contains(c.ChargeId)).Select(c => new { c.ChargeId, c.LegalStatus, c.Provisional }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var chargeOf = charges.ToDictionary(c => c.ChargeId);
        return [.. rows.GroupBy(r => (r.Period, LevyType: r.ChargeType ?? "UNSPECIFIED", r.Currency)).OrderBy(g => g.Key.Period).ThenBy(g => g.Key.LevyType).ThenBy(g => g.Key.Currency)
            .Select(g => new LevyBucket(
                g.Key.Period, g.Key.LevyType, g.Key.Currency, g.Aggregate(0m, (sum, r) => sum + Signed(r.Side, r.Amount)),
                LevyLegal.Weakest(g.Select(r => r.ChargeId is { } id && chargeOf.TryGetValue(id, out var c) ? (c.LegalStatus, c.Provisional) : ((string?)null, true)))))];
    }

    /// <summary>One levy authority in one month and currency.</summary>
    internal sealed record LevyBucket(string Period, string LevyType, string Currency, decimal Net, string LegalStatus);

    /// <summary>True when <paramref name="period"/> is a calendar month <c>yyyy-MM</c>.</summary>
    public static bool IsPeriod(string period) => period.Length == 7 && period[4] == '-' && int.TryParse(period.AsSpan(0, 4), out var y) && y is > 1999 and < 2200
        && int.TryParse(period.AsSpan(5, 2), out var m) && m is >= 1 and <= 12;

    private async Task<List<Bucket>> BucketsAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var rows = await LinesAsync(legalEntity, period, cancellationToken).ConfigureAwait(false);
        return [.. rows.GroupBy(r => (r.Period, r.AccountCode, r.Currency)).OrderBy(g => g.Key.Period).ThenBy(g => g.Key.AccountCode)
            .Select(g => new Bucket(g.Key.Period, Kind(g.Key.AccountCode), g.Key.Currency, g.Sum(r => Signed(r.Side, r.Amount)), g.Count()))];
    }

    private async Task<List<LevyLine>> LinesAsync(LegalEntityId legalEntity, string? period, CancellationToken cancellationToken)
    {
        var query = from line in db.LedgerLines.AsNoTracking()
                    join entry in db.LedgerEntries.AsNoTracking() on line.EntryId equals entry.EntryId
                    where entry.LegalEntityId == legalEntity && PayableAccounts.Contains(line.AccountCode)
                    select new { entry.AccountingDate, line.AccountCode, line.RuleId, line.Side, line.Currency, line.Amount, line.ChargeType, line.ChargeId, line.SourceType };
        if (period is not null)
        {
            var first = new BusinessDate(int.Parse(period[..4], CultureInfo.InvariantCulture), int.Parse(period[5..], CultureInfo.InvariantCulture), 1);
            var next = first.AddMonths(1);
            query = query.Where(l => l.AccountingDate >= first && l.AccountingDate < next);
        }

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new LevyLine(r.AccountingDate.Year.ToString("0000", CultureInfo.InvariantCulture) + "-" + r.AccountingDate.Month.ToString("00", CultureInfo.InvariantCulture), r.AccountCode, r.RuleId, r.Side, r.Currency, r.Amount, r.ChargeType, r.ChargeId, r.SourceType))];
    }

    private static string Kind(string accountCode) => accountCode == "LA-06" ? "IPT" : "LEVY";

    private static decimal Signed(string side, decimal amount) => string.Equals(side, "CREDIT", StringComparison.OrdinalIgnoreCase) ? amount : -amount;

    private sealed record LevyLine(string Period, string AccountCode, string RuleId, string Side, string Currency, decimal Amount, string? ChargeType, ChargeId? ChargeId, string? SourceType);
}

/// <summary>Legal status of the rows behind a levy (D-USR-19): only Settled (or not regulatory) levy is paid in Production.</summary>
internal static class LevyLegal
{
    public const string Settled = "Settled";
    public const string PendingOpinion = "PendingOpinion";
    public const string Unverified = "Unverified";

    /// <summary>The weakest status of the lines: PendingOpinion, else Unverified (provisional, missing or unknown status), else Settled.</summary>
    public static string Weakest(IEnumerable<(string? Status, bool Provisional)> lines)
    {
        var weakest = Settled;
        foreach (var (status, provisional) in lines)
        {
            if (string.Equals(status, PendingOpinion, StringComparison.Ordinal))
            {
                return PendingOpinion;
            }

            if (provisional || !(string.Equals(status, Settled, StringComparison.Ordinal) || string.Equals(status, "NotRegulatory", StringComparison.Ordinal)))
            {
                weakest = Unverified;
            }
        }

        return weakest;
    }
}

/// <summary>Where the payment request of a levy payable stands, from the state of its disbursement.</summary>
internal enum LevyPaymentState
{
    None,
    Requested,
    Approved,
    Paid,
    Dead,
}

internal static class LevyPaymentStates
{
    public static LevyPaymentState Of(string disbursementState) => disbursementState switch
    {
        "REQUESTED" or "PENDING_APPROVAL" => LevyPaymentState.Requested,
        "APPROVED" or "RELEASED" => LevyPaymentState.Approved,
        "ISSUED" or "CLEARED" => LevyPaymentState.Paid,
        _ => LevyPaymentState.Dead,
    };
}
