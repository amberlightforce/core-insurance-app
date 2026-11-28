using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Queries;

/// <summary>
/// Commission resolution and simulation (BO-PTY-11, REQ-PTY-010). Read only: finds the producers of record on a date (BO-PTY-09), the ACTIVE
/// agreement versions in force (BO-PTY-10) and the winning rate line (<see cref="CommissionResolver"/>), and computes commission amounts rounded
/// by the MKT rounding rule and split with the cent allocator.
/// </summary>
internal sealed class CommissionResolution(PartyDbContext db, IMarketRoundingService rounding, PartyProtection protection, RequestContext context)
{
    private const string RoundingPurpose = "charge.line";
    private const int MaxSimulateLines = 200;

    private sealed record Producer(IntermediaryId IntermediaryId, string? ProducerCode, string Role, decimal SharePct, IReadOnlyList<string> Explanation);

    private sealed record ResolvedPair(Producer Producer, ResolvedRate Rate);

    private sealed record Resolution(
        IReadOnlyList<CommissionResolvedProducer> Producers, IReadOnlyList<ResolvedPair> Resolved, CommissionAgreementResolveResponse.ReasonValue? Reason, string? ReasonText);

    public async Task<Result<CommissionAgreementResolveResponse>> ResolveAsync(CommissionAgreementResolveRequest request, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var outcome = await ResolveCoreAsync(
            legalEntity, request.PolicyId, request.IntermediaryId, request.ProductCode, request.LineOfBusiness, request.Date, BusinessTypeWire(request.BusinessType),
            BasisWire(request.Basis), cancellationToken).ConfigureAwait(false);
        var resolved = outcome.Resolved.Count > 0;
        return new CommissionAgreementResolveResponse
        {
            Date = request.Date,
            Outcome = resolved ? CommissionAgreementResolveResponse.OutcomeValue.Resolved : CommissionAgreementResolveResponse.OutcomeValue.NoCommission,
            Reason = resolved ? null : outcome.Reason,
            ReasonText = resolved ? null : outcome.ReasonText,
            Producers = outcome.Producers,
        };
    }

    public async Task<Result<CommissionAgreementSimulateResponse>> SimulateAsync(CommissionAgreementSimulateRequest request, CancellationToken cancellationToken)
    {
        if (request.Lines.Count is 0 or > MaxSimulateLines)
        {
            return DomainError.Of(ModuleCode.PTY, "VALIDATION", $"A simulation needs between 1 and {MaxSimulateLines} premium lines.");
        }

        var currency = request.Lines[0].Premium.Currency;
        if (request.Lines.Any(l => l.Premium.Currency != currency || l.Premium.Amount < 0m))
        {
            return DomainError.Of(ModuleCode.PTY, "VALIDATION", "Premium lines must be zero or more and in one currency.");
        }

        var legalEntity = protection.Current(context);
        var cache = new Dictionary<string, Resolution>(StringComparer.Ordinal);
        var lines = new List<CommissionSimulatedLine>(request.Lines.Count);
        var total = 0m;
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var date = line.Date ?? request.Date;
            var businessType = BusinessTypeWire(line.BusinessType);
            var basis = BasisWire(line.Basis);
            var key = string.Join('|', date, line.LineOfBusiness ?? "*", businessType, basis);
            if (!cache.TryGetValue(key, out var resolution))
            {
                resolution = await ResolveCoreAsync(
                    legalEntity, request.PolicyId, request.IntermediaryId, request.ProductCode, line.LineOfBusiness, date, businessType, basis, cancellationToken).ConfigureAwait(false);
                cache[key] = resolution;
            }

            if (resolution.Resolved.Count == 0)
            {
                lines.Add(new CommissionSimulatedLine
                {
                    LineIndex = i,
                    Premium = line.Premium,
                    Outcome = CommissionSimulatedLine.OutcomeValue.NoCommission,
                    Reason = resolution.Reason is { } reason ? ReasonWire(reason) : null,
                    ReasonText = resolution.ReasonText,
                    Commission = new Money(0m, currency),
                    Allocations = [],
                    Unresolved = resolution.Producers,
                });
                continue;
            }

            var exacts = resolution.Resolved.Select(p => line.Premium.Amount * p.Rate.Line.RatePct / 100m * p.Producer.SharePct / 100m).ToList();
            var lineTotal = await RoundAsync(exacts.Sum(), currency, legalEntity, date, cancellationToken).ConfigureAwait(false);
            var parts = CentAllocation.Distribute(lineTotal, exacts);
            total += lineTotal;
            lines.Add(new CommissionSimulatedLine
            {
                LineIndex = i,
                Premium = line.Premium,
                Outcome = CommissionSimulatedLine.OutcomeValue.Resolved,
                Commission = new Money(lineTotal, currency),
                Allocations =
                [
                    .. resolution.Resolved.Select((p, k) => new CommissionAllocation
                    {
                        IntermediaryId = p.Producer.IntermediaryId,
                        ProducerCode = p.Producer.ProducerCode,
                        SharePct = p.Producer.SharePct,
                        RatePct = p.Rate.Line.RatePct,
                        Commission = new Money(parts[k], currency),
                        VersionId = p.Rate.Version.VersionId,
                        Explanation = [.. p.Producer.Explanation, .. p.Rate.Explanation],
                    }),
                ],
                Unresolved = [.. resolution.Producers.Where(p => !p.Resolved)],
            });
        }

        return new CommissionAgreementSimulateResponse { Lines = lines, TotalCommission = new Money(total, currency) };
    }

    private async Task<Resolution> ResolveCoreAsync(
        LegalEntityId legalEntity, PolicyId? policyId, IntermediaryId? intermediaryId, string? productCode, string? lineOfBusiness, BusinessDate date, string businessType,
        string basis, CancellationToken cancellationToken)
    {
        var producers = await ProducersAsync(legalEntity, policyId, intermediaryId, date, cancellationToken).ConfigureAwait(false);
        if (producers.Count == 0)
        {
            return new Resolution(
                [], [], CommissionAgreementResolveResponse.ReasonValue.NoProducer,
                "No producer is known: the policy has no producer-of-record change on this date and no intermediary was given.");
        }

        var result = new List<CommissionResolvedProducer>();
        var resolved = new List<ResolvedPair>();
        foreach (var producer in producers)
        {
            var role = producer.Role == "PRODUCER" ? CommissionResolvedProducer.RoleValue.Producer : CommissionResolvedProducer.RoleValue.PreviousProducer;
            var versions = await ActiveVersionsAsync(legalEntity, producer.IntermediaryId, cancellationToken).ConfigureAwait(false);
            var (rate, miss) = CommissionResolver.Resolve(versions, productCode, lineOfBusiness, date, businessType, basis);
            if (rate is not null)
            {
                resolved.Add(new ResolvedPair(producer, rate));
                result.Add(new CommissionResolvedProducer
                {
                    IntermediaryId = producer.IntermediaryId,
                    ProducerCode = producer.ProducerCode,
                    Role = role,
                    SharePct = producer.SharePct,
                    Resolved = true,
                    AgreementId = rate.Version.AgreementId,
                    VersionId = rate.Version.VersionId,
                    VersionNo = rate.Version.VersionNo,
                    ScopeKey = rate.Version.ScopeKey,
                    RateLine = CommissionAgreementQueries.ToLine(new CommissionRateLineRow
                    {
                        VersionId = rate.Version.VersionId, LineNo = rate.Line.LineNo, BusinessType = rate.Line.BusinessType, LineOfBusiness = rate.Line.LineOfBusiness,
                        Basis = rate.Line.Basis, RatePct = rate.Line.RatePct,
                    }),
                    Explanation = [.. producer.Explanation, .. rate.Explanation],
                });
            }
            else
            {
                result.Add(new CommissionResolvedProducer
                {
                    IntermediaryId = producer.IntermediaryId,
                    ProducerCode = producer.ProducerCode,
                    Role = role,
                    SharePct = producer.SharePct,
                    Resolved = false,
                    Reason = miss!.Reason == "NO_AGREEMENT" ? CommissionResolvedProducer.ReasonValue.NoAgreement : CommissionResolvedProducer.ReasonValue.NoRateLine,
                    Explanation = [.. producer.Explanation, .. miss.Explanation],
                });
            }
        }

        if (resolved.Count > 0)
        {
            return new Resolution(result, resolved, null, null);
        }

        var allNoRateLine = result.All(p => p.Reason == CommissionResolvedProducer.ReasonValue.NoRateLine);
        return new Resolution(
            result, [],
            allNoRateLine ? CommissionAgreementResolveResponse.ReasonValue.NoRateLine : CommissionAgreementResolveResponse.ReasonValue.NoAgreement,
            allNoRateLine ? "An agreement is in force but has no rate line for this business type and premium basis." : "No active commission agreement is in force for the producer on this date.");
    }

    /// <summary>The producers earning the commission on the date, from the producer of record and its consequence (BO-PTY-09).</summary>
    private async Task<IReadOnlyList<Producer>> ProducersAsync(
        LegalEntityId legalEntity, PolicyId? policyId, IntermediaryId? intermediaryId, BusinessDate date, CancellationToken cancellationToken)
    {
        if (policyId is { } policy)
        {
            var rows = await db.ProducerOfRecords.AsNoTracking()
                .Where(r => r.LegalEntityId == legalEntity && r.PolicyId == policy && r.RecordedTo == null)
                .OrderBy(r => r.ValidFrom).ToListAsync(cancellationToken).ConfigureAwait(false);
            var current = rows.LastOrDefault(r => r.ValidFrom <= date && (r.ValidTo is null || date < r.ValidTo));
            if (current is not null)
            {
                var previous = rows.Where(r => r.ValidFrom < current.ValidFrom && r.PorId != current.PorId).OrderByDescending(r => r.ValidFrom).FirstOrDefault();
                var head = $"Producer of record on {date} is {current.ProducerCode} (consequence {current.CommissionConsequence}).";
                if (previous is not null && current.CommissionConsequence == "OLD_PRODUCER_KEEPS_ALL")
                {
                    return [new Producer(previous.IntermediaryId, previous.ProducerCode, "PREVIOUS_PRODUCER", 100m, [head, $"The previous producer {previous.ProducerCode} keeps all commission on the term."])];
                }

                if (previous is not null && current.SplitShare is { } share and > 0m and < 100m)
                {
                    return
                    [
                        new Producer(current.IntermediaryId, current.ProducerCode, "PRODUCER", share, [head, $"Commission is split: {share}% to {current.ProducerCode}."]),
                        new Producer(previous.IntermediaryId, previous.ProducerCode, "PREVIOUS_PRODUCER", 100m - share, [head, $"Commission is split: {100m - share}% stays with {previous.ProducerCode}."]),
                    ];
                }

                return [new Producer(current.IntermediaryId, current.ProducerCode, "PRODUCER", 100m, [head])];
            }
        }

        // TODO(hardening): with only a policy and no producer-of-record change, the term's own producer code is not looked up here; callers pass intermediaryId.
        return intermediaryId is { } direct
            ? [new Producer(direct, null, "PRODUCER", 100m, ["The producer is the intermediary given in the request."])]
            : [];
    }

    private async Task<IReadOnlyList<ResolverVersion>> ActiveVersionsAsync(LegalEntityId legalEntity, IntermediaryId intermediaryId, CancellationToken cancellationToken)
    {
        var versions = await (
            from v in db.CommissionVersions.AsNoTracking()
            join a in db.CommissionAgreements.AsNoTracking() on v.AgreementId equals a.AgreementId
            where a.IntermediaryId == intermediaryId && a.LegalEntityId == legalEntity && v.Status == CommissionStatus.Active
            select new { v.VersionId, v.AgreementId, v.VersionNo, v.EffectiveFrom, a.ProductCode, a.LineOfBusiness, a.ScopeKey }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return [];
        }

        var ids = versions.Select(v => v.VersionId).ToList();
        var lines = await db.CommissionRateLines.AsNoTracking().Where(l => ids.Contains(l.VersionId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. versions.Select(v => new ResolverVersion(
                v.AgreementId, v.VersionId, v.VersionNo, v.ProductCode, v.LineOfBusiness, v.ScopeKey, v.EffectiveFrom,
                [.. lines.Where(l => l.VersionId == v.VersionId).Select(l => new ResolverLine(l.LineNo, l.BusinessType, l.LineOfBusiness, l.Basis, l.RatePct))])),
        ];
    }

    /// <summary>MKT rounding (<c>mkt.Rounding.apply</c>); the platform default (half up, 2 places) when MKT returns no amount.</summary>
    private async Task<decimal> RoundAsync(decimal amount, Currency currency, LegalEntityId legalEntity, BusinessDate validAt, CancellationToken cancellationToken)
    {
        var response = await rounding.ApplyAsync(
            new RoundingApplyRequest
            {
                Amount = new Money(amount, currency),
                Currency = currency,
                Purpose = RoundingPurpose,
                Context = new RoundingApplyRequest.ContextDetail { LegalEntity = context.LegalEntity!.Value.Value, ValidAt = validAt },
            }, cancellationToken).ConfigureAwait(false);
        return response.AmountAfterRounding is { } money && money.Currency == currency
            ? money.Amount
            : DecimalRounding.Round(amount, 2, DecimalRounding.DefaultMoneyMode);
    }

    private static string BusinessTypeWire(CommissionAgreementResolveRequest.BusinessTypeValue value) =>
        value == CommissionAgreementResolveRequest.BusinessTypeValue.NewBusiness ? "NEW_BUSINESS" : "RENEWAL";

    private static string BusinessTypeWire(CommissionSimulateLine.BusinessTypeValue value) =>
        value == CommissionSimulateLine.BusinessTypeValue.NewBusiness ? "NEW_BUSINESS" : "RENEWAL";

    private static string BasisWire(CommissionAgreementResolveRequest.BasisValue value) =>
        value == CommissionAgreementResolveRequest.BasisValue.NetPremium ? "NET_PREMIUM" : "GROSS_PREMIUM";

    private static string BasisWire(CommissionSimulateLine.BasisValue value) =>
        value == CommissionSimulateLine.BasisValue.NetPremium ? "NET_PREMIUM" : "GROSS_PREMIUM";

    private static string ReasonWire(CommissionAgreementResolveResponse.ReasonValue value) => value switch
    {
        CommissionAgreementResolveResponse.ReasonValue.NoProducer => "NO_PRODUCER",
        CommissionAgreementResolveResponse.ReasonValue.NoRateLine => "NO_RATE_LINE",
        CommissionAgreementResolveResponse.ReasonValue.ProducerKeepsAllNotFound => "PRODUCER_KEEPS_ALL_NOT_FOUND",
        _ => "NO_AGREEMENT",
    };
}
