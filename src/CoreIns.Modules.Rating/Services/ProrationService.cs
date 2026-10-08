using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Rating.Services;

/// <summary>
/// The pure part of <c>rat.Proration.prorate</c> (REQ-RAT-004, -155..-158, -160, -165, D-SL3-04): validation, day count and
/// the exact unrounded amount. No I/O, no clock, no floating point. Days are whole Europe/Athens calendar dates, half-open:
/// <c>days(from, to) = to - from</c>.
/// </summary>
internal static class ProrationCalculator
{
    /// <summary>Places kept in <see cref="PlannedLine.Unrounded"/> and the informational fraction. Truncated toward zero, so the stored value never exceeds the true one.</summary>
    public const int Places = 16;

    /// <summary>Upper bound on lines per request (segments x lines), so a request cannot be used to exhaust the service.</summary>
    public const int MaxLines = 20_000;

    /// <summary>A line with its day count and exact unrounded amount (before MKT rounding).</summary>
    public sealed record PlannedLine(
        ProrationSegmentInput Segment, ProrationLineInput Line, int Days, int Denominator, decimal Fraction, decimal Unrounded);

    /// <summary>Validated plan of a request.</summary>
    public sealed record Plan(string Convention, int TermDays, IReadOnlyList<PlannedLine> Lines);

    public static Plan Prepare(ProrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Currency.IsDefault)
        {
            throw Error("INPUT", "A currency is required.");
        }

        var convention = DayCountConventions.Normalise(request.Convention);
        var declared = (request.DeclaredConventions ?? []).Select(DayCountConventions.Normalise).Where(c => c is not null).ToHashSet(StringComparer.Ordinal);
        if (convention is null || declared.Count == 0 || !declared.Contains(convention))
        {
            throw Error("CONVENTION", $"The day-count convention '{request.Convention}' is not declared by the product artefact; RAT never substitutes one (REQ-RAT-165).");
        }

        if (convention is not (DayCountConventions.TermRatio or DayCountConventions.Act365Fixed))
        {
            throw Error("CONVENTION", $"The day-count convention '{convention}' is declared but not built; only TERM_RATIO and ACT/365F are (REQ-RAT-156).");
        }

        if (request.TermTo <= request.TermFrom)
        {
            throw Error("PERIOD", $"The term [{request.TermFrom}, {request.TermTo}) has no days; the term must end after it starts.");
        }

        if (request.Segments is null || request.Segments.Count == 0)
        {
            throw Error("INPUT", "Send at least one segment.");
        }

        var termDays = request.TermTo.DaysSince(request.TermFrom);
        var denominator = convention == DayCountConventions.TermRatio ? termDays : 365;
        var lines = new List<PlannedLine>();
        foreach (var segment in request.Segments)
        {
            if (segment.To < segment.From)
            {
                throw Error("PERIOD", $"Segment {segment.SegmentId} ends before it starts: [{segment.From}, {segment.To}).");
            }

            if (segment.From < request.TermFrom || segment.To > request.TermTo)
            {
                throw Error("PERIOD", $"Segment {segment.SegmentId} [{segment.From}, {segment.To}) is not inside the term [{request.TermFrom}, {request.TermTo}).");
            }

            var days = segment.To.DaysSince(segment.From);
            foreach (var line in segment.Lines ?? [])
            {
                if (lines.Count >= MaxLines)
                {
                    throw Error("INPUT", $"Send at most {MaxLines} lines.");
                }

                lines.Add(PlanLine(segment, line, days, denominator));
            }
        }

        if (lines.Count == 0)
        {
            throw Error("INPUT", "Send at least one line.");
        }

        return new Plan(convention, termDays, lines);
    }

    private static PlannedLine PlanLine(ProrationSegmentInput segment, ProrationLineInput line, int days, int denominator)
    {
        if (line.Handling != ProrationHandling.Proratable)
        {
            // Flat charges are never prorated and fully-earned charges are returned in full for their segment (REQ-RAT-160).
            return new PlannedLine(segment, line, days, 1, 1m, line.AnnualRate);
        }

        // Compute on the magnitude and re-apply the sign, so the reversal of a credit is the exact negative of the debit.
        var magnitude = Math.Abs(line.AnnualRate);
        try
        {
            var numerator = ExactDecimal.Multiply(magnitude, days);
            var unrounded = ExactDecimal.Divide(numerator, denominator, Places, MidpointRounding.ToZero);
            var fraction = ExactDecimal.Divide(days, denominator, Places, MidpointRounding.ToZero);
            return new PlannedLine(segment, line, days, denominator, fraction, line.AnnualRate < 0m ? -unrounded : unrounded);
        }
        catch (PrecisionLossException ex)
        {
            throw Error("SCALE", $"The annual rate of line {line.LineId} cannot be prorated without losing precision: {ex.Message}");
        }
    }

    internal static DomainException Error(string code, string detail) => new(DomainError.Of(ModuleCode.RAT, code, detail));
}

/// <summary>
/// <c>rat.Proration.prorate</c> (REQ-RAT-004, -155, -156, -159, -163, -165): annual rates become amounts. The unrounded amount is
/// exact (see <see cref="ProrationCalculator"/>); rounding is explicit and goes through MKT's <c>charge.line</c> rule, applied to
/// the magnitude and re-signed so a reversal is the exact negative (REQ-RAT-163). The rounding residual (unrounded - rounded) is
/// returned so POL can keep cumulative amounts exact. Minimum premium and short-rate are not applied here (D-SL3-04).
/// </summary>
internal sealed class RatingProrationEngine(IMarketRoundingService rounding, RequestContext context) : IRatingProrationEngine
{
    /// <summary>MKT rounding purpose for premium lines (PRD-17 REQ-MKT-192; the catalogue's <c>cur.rounding.charge.line</c>).</summary>
    internal const string RoundingPurpose = "charge.line";

    public async Task<ProrationResult> ProrateAsync(ProrationRequest request, CancellationToken cancellationToken = default)
    {
        var plan = ProrationCalculator.Prepare(request);
        var legalEntity = context.LegalEntity?.Value
            ?? throw new InvalidOperationException("The request context has no legal entity.");
        var validAt = request.RoundingDate ?? request.TermFrom;

        var cache = new Dictionary<decimal, RoundingApplyResponse>();
        var statuses = new List<string>();
        var provisional = false;
        var lines = new List<ProratedLine>(plan.Lines.Count);
        foreach (var planned in plan.Lines)
        {
            var magnitude = Math.Abs(planned.Unrounded);
            RoundingApplyResponse? outcome = null;
            decimal roundedMagnitude;
            if (magnitude == 0m)
            {
                roundedMagnitude = 0m; // nothing to round: no rule is consulted
            }
            else
            {
                if (!cache.TryGetValue(magnitude, out outcome))
                {
                    outcome = await rounding.ApplyAsync(
                        new RoundingApplyRequest
                        {
                            Amount = new Money(magnitude, request.Currency),
                            Currency = request.Currency,
                            Purpose = RoundingPurpose,
                            Context = new RoundingApplyRequest.ContextDetail { LegalEntity = legalEntity, ValidAt = validAt },
                        },
                        cancellationToken).ConfigureAwait(false);
                    cache[magnitude] = outcome;
                }

                roundedMagnitude = outcome.AmountAfterRounding?.Amount
                    ?? throw ProrationCalculator.Error("DATA-UNAVAILABLE", "MKT rounding returned no amount; proration fails closed.");
                if (outcome.AmountAfterRounding!.Value.Currency != request.Currency)
                {
                    throw ProrationCalculator.Error("CURRENCY", "MKT rounding answered in another currency.");
                }

                if (outcome.LegalStatus is { } status)
                {
                    statuses.Add(status);
                }

                provisional |= outcome.Provisional == true;
            }

            var sign = planned.Unrounded < 0m ? -1m : 1m;
            var amount = roundedMagnitude == 0m ? 0m : sign * roundedMagnitude;
            var residual = planned.Unrounded - amount; // unrounded - rounded, same sign as the rate
            lines.Add(new ProratedLine(
                planned.Segment.SegmentId,
                planned.Line.LineId,
                planned.Line.ElementId,
                planned.Line.ChargeType,
                planned.Line.Handling,
                planned.Days,
                planned.Denominator,
                planned.Fraction,
                planned.Unrounded,
                new Money(amount, request.Currency),
                new Money(residual, request.Currency),
                outcome?.RuleKey,
                outcome?.RuleId));
        }

        return new ProrationResult(plan.Convention, plan.TermDays, request.ConfigurationHash, lines, Weakest(statuses), provisional);
    }

    private static string? Weakest(List<string> statuses) =>
        statuses.Count == 0 ? null : statuses.MaxBy(Strength);

    /// <summary>Order of legal statuses, weakest highest (same order as the tax stage of <c>rat.Rate.rate</c>).</summary>
    internal static int Strength(string status) => status switch
    {
        "NotRegulatory" => 0,
        "Settled" => 1,
        "Verify" => 2,
        "PendingOpinion" => 3,
        "Uncertain" or "MarketPractice" => 4,
        "Unverified" => 5,
        _ => 6,
    };

}
