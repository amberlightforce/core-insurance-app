using System.Text.Json;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Handling = CoreIns.Modules.Rating.Contracts.Api.ProrationAnnualRate.HandlingValue;

namespace CoreIns.Modules.Rating.Services;

/// <summary>One annual-rate line of a segment, in the calculator's own model.</summary>
internal sealed record CalcLine(string SegmentId, string Element, string Coverage, string ChargeType, string Category, decimal Annual, Handling Handling)
{
    /// <summary>The identity of a line across segments (rates may differ per segment, but overlap and remainder checks are per line).</summary>
    public string Key => $"{Element}|{Coverage}|{ChargeType}|{Category}";
}

/// <summary>A segment: half-open <c>[From, To)</c> in whole Europe/Athens dates. <c>From == To</c> is a zero-day segment.</summary>
internal sealed record CalcSegment(string SegmentId, BusinessDate From, BusinessDate To, bool Reverse);

/// <summary>The calculator's input.</summary>
internal sealed record CalcInput(
    Currency Currency, BusinessDate TermFrom, BusinessDate TermTo, DayCountConvention Convention, IReadOnlyList<CalcSegment> Segments, IReadOnlyList<CalcLine> Lines);

/// <summary>
/// The pure part of <c>rat.Proration.prorate</c> (REQ-RAT-004, -155..-158, -160, -165, D-SL3-04): validation, day count and the
/// exact unrounded amount. No I/O, no clock, no floating point. Days are whole Europe/Athens calendar dates, half-open:
/// <c>days(from, to) = to - from</c>.
/// <list type="bullet">
/// <item><b>TERM_RATIO</b>: annual rate x (segment days / term days) x (term length in years), where the term length is its whole calendar months / 12 (a six-month term is 0.5), so a six-month term of annual rate 365 is worth 182.50 and a one-year term is the rate (REQ-RAT-276).</item>
/// <item><b>ACT/365F</b>: annual rate x segment days / 365.</item>
/// <item>Under TERM_RATIO, when the segments of a line (all at the same rate) cover the whole term contiguously, the last one takes the term amount minus the others, so the unrounded sum is exactly the term amount (REQ-RAT-158).</item>
/// <item><b>Flat</b> charges are charged in full only in the inception segment (the one starting on the term start); a change segment gets none (REQ-RAT-160, REQ-PFC-114). <b>Fully-earned</b> charges are returned in full in the segment they are supplied for.</item>
/// </list>
/// </summary>
internal static class ProrationCalculator
{
    /// <summary>Places kept in <see cref="PlannedLine.Unrounded"/> and the fraction. Truncated toward zero.</summary>
    public const int Places = 16;

    /// <summary>Upper bound on lines per request.</summary>
    public const int MaxLines = 20_000;

    /// <summary>A line with its day count and exact unrounded amount (before MKT rounding), signed like the rate.</summary>
    public sealed record PlannedLine(CalcSegment Segment, CalcLine Line, int Days, decimal Fraction, decimal Unrounded);

    /// <summary>Validated plan of a request.</summary>
    public sealed record Plan(int TermDays, int TermMonths, IReadOnlyList<PlannedLine> Lines);

    public static Plan Prepare(CalcInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Currency.IsDefault)
        {
            throw Error("INPUT", "A currency is required.");
        }

        if (input.Convention is not (DayCountConvention.TermRatio or DayCountConvention.Act365f))
        {
            throw Error("CONVENTION", $"The day-count convention {input.Convention} is not built; only TERM_RATIO and ACT_365F are (REQ-RAT-156).");
        }

        if (input.TermTo <= input.TermFrom)
        {
            throw Error("PERIOD", $"The term [{input.TermFrom}, {input.TermTo}) has no days; the term must end after it starts.");
        }

        if (input.Segments.Count == 0 || input.Lines.Count == 0)
        {
            throw Error("INPUT", "Send at least one period and one annual rate.");
        }

        if (input.Lines.Count > MaxLines)
        {
            throw Error("INPUT", $"Send at most {MaxLines} lines.");
        }

        var termDays = input.TermTo.DaysSince(input.TermFrom);
        var months = input.Convention == DayCountConvention.TermRatio ? WholeMonths(input.TermFrom, input.TermTo) : 12;
        var segments = new Dictionary<string, CalcSegment>(StringComparer.Ordinal);
        foreach (var segment in input.Segments)
        {
            if (!segments.TryAdd(segment.SegmentId, segment))
            {
                throw Error("INPUT", $"Segment {segment.SegmentId} is listed twice.");
            }

            if (segment.To < segment.From)
            {
                throw Error("PERIOD", $"Segment {segment.SegmentId} ends before it starts: [{segment.From}, {segment.To}).");
            }

            if (segment.From < input.TermFrom || segment.To > input.TermTo)
            {
                throw Error("PERIOD", $"Segment {segment.SegmentId} [{segment.From}, {segment.To}) is not inside the term [{input.TermFrom}, {input.TermTo}).");
            }
        }

        foreach (var group in input.Lines.GroupBy(l => l.Key, StringComparer.Ordinal))
        {
            var spans = group.Select(l => segments.TryGetValue(l.SegmentId, out var s) ? s : throw Error("INPUT", $"An annual rate names segment {l.SegmentId}, which is not in periods."))
                .OrderBy(s => s.From).ThenBy(s => s.To).ToList();
            for (var i = 1; i < spans.Count; i++)
            {
                if (spans[i].From < spans[i - 1].To)
                {
                    throw Error("PERIOD", $"Segments {spans[i - 1].SegmentId} and {spans[i].SegmentId} overlap for line {group.Key}.");
                }
            }
        }

        var planned = input.Lines.Select(l => PlanLine(input, segments[l.SegmentId], l, termDays, months)).ToList();
        if (input.Convention == DayCountConvention.TermRatio)
        {
            ApplyRemainder(planned, input, months);
        }

        return new Plan(termDays, months, planned);
    }

    /// <summary>Whole calendar months between the term start and end (a term is whole months, or TERM_RATIO has no term length).</summary>
    internal static int WholeMonths(BusinessDate from, BusinessDate to)
    {
        var months = ((to.Year - from.Year) * 12) + (to.Month - from.Month);
        return months > 0 && from.AddMonths(months) == to
            ? months
            : throw Error("PERIOD", $"Under TERM_RATIO the term [{from}, {to}) must be a whole number of calendar months.");
    }

    private static PlannedLine PlanLine(CalcInput input, CalcSegment segment, CalcLine line, int termDays, int months)
    {
        var days = segment.To.DaysSince(segment.From);
        switch (line.Handling)
        {
            case Handling.Flat:
                // A flat charge belongs to inception; a mid-term change segment carries none.
                return segment.From == input.TermFrom
                    ? new PlannedLine(segment, line, days, 1m, line.Annual)
                    : new PlannedLine(segment, line, days, 0m, 0m);
            case Handling.FullyEarned:
                return new PlannedLine(segment, line, days, 1m, line.Annual);
        }

        // Compute on the magnitude and re-apply the sign, so the reversal of a credit is the exact negative of the debit.
        var magnitude = Math.Abs(line.Annual);
        try
        {
            decimal numerator;
            decimal fractionNumerator;
            decimal denominator;
            if (input.Convention == DayCountConvention.TermRatio)
            {
                fractionNumerator = ExactDecimal.Multiply(days, months);
                numerator = ExactDecimal.Multiply(magnitude, fractionNumerator);
                denominator = ExactDecimal.Multiply(termDays, 12);
            }
            else
            {
                fractionNumerator = days;
                numerator = ExactDecimal.Multiply(magnitude, days);
                denominator = 365;
            }

            var unrounded = ExactDecimal.Divide(numerator, denominator, Places, MidpointRounding.ToZero);
            var fraction = ExactDecimal.Divide(fractionNumerator, denominator, Places, MidpointRounding.ToZero);
            return new PlannedLine(segment, line, days, fraction, line.Annual < 0m ? -unrounded : unrounded);
        }
        catch (PrecisionLossException ex)
        {
            throw Error("SCALE", $"The annual rate of a {line.ChargeType} line cannot be prorated without losing precision: {ex.Message}");
        }
    }

    /// <summary>REQ-RAT-158: when the segments of a line at one rate cover the term contiguously, the last takes what is left of the term amount.</summary>
    private static void ApplyRemainder(List<PlannedLine> planned, CalcInput input, int months)
    {
        foreach (var group in planned.Where(p => p.Line.Handling == Handling.Proratable).GroupBy(p => p.Line.Key, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(p => p.Segment.From).ThenBy(p => p.Segment.To).ToList();
            var sameRate = ordered.All(p => p.Line.Annual == ordered[0].Line.Annual);
            var covers = ordered[0].Segment.From == input.TermFrom && ordered[^1].Segment.To == input.TermTo
                && Enumerable.Range(1, ordered.Count - 1).All(i => ordered[i].Segment.From == ordered[i - 1].Segment.To);
            if (!sameRate || !covers)
            {
                continue;
            }

            var magnitude = Math.Abs(ordered[0].Line.Annual);
            var term = ExactDecimal.Divide(ExactDecimal.Multiply(magnitude, months), 12, Places, MidpointRounding.ToZero);
            var others = ordered.Take(ordered.Count - 1).Sum(p => Math.Abs(p.Unrounded));
            var last = ordered[^1];
            var remainder = term - others;
            planned[planned.IndexOf(last)] = last with { Unrounded = ordered[0].Line.Annual < 0m ? -remainder : remainder };
        }
    }

    internal static DomainException Error(string code, string detail) => new(DomainError.Of(ModuleCode.RAT, code, detail));
}

/// <summary>
/// <c>rat.Proration.prorate</c> (REQ-RAT-004, -155, -156, -158, -160, -163, -165): annual rates become amounts.
/// <list type="bullet">
/// <item>The convention is the one the product artefact declares (<c>dayCount</c>, read by <c>productArtefactHash</c>); the caller's
/// convention must equal it, otherwise <c>RAT-ERR-CONVENTION</c>. A provisional day count is reported in the explanation.</item>
/// <item>Rounding is explicit and goes through MKT's <c>charge.line</c> rule on the magnitude, re-signed, so a reversal is the exact negative.
/// The residual (unrounded - rounded) is returned so POL can keep cumulative amounts exact. A period with <c>reverse</c> is negated.</item>
/// <item>Minimum premium and short-rate are not applied here (D-SL3-04).</item>
/// </list>
/// </summary>
internal sealed class RatingProrationEngine(IMarketRoundingService rounding, IProductArtifactService products, RequestContext context) : IRatingProrationEngine
{
    /// <summary>MKT rounding purpose for premium lines (PRD-17 REQ-MKT-192; the catalogue's <c>cur.rounding.charge.line</c>).</summary>
    internal const string RoundingPurpose = "charge.line";

    public async Task<ProrationProrateResponse> ProrateAsync(ProrationProrateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (declared, provisional, note) = await DeclaredConventionAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Convention != declared)
        {
            throw ProrationCalculator.Error("CONVENTION", $"The product artefact declares {declared}; the request asks for {request.Convention} (REQ-RAT-165).");
        }

        var input = Map(request);
        var plan = ProrationCalculator.Prepare(input);
        var reverse = request.Periods.ToDictionary(p => p.SegmentId, p => p.Reverse == true, StringComparer.Ordinal);
        var legalEntity = context.LegalEntity?.Value ?? throw new InvalidOperationException("The request context has no legal entity.");

        var cache = new Dictionary<decimal, RoundingApplyResponse>();
        var lines = new List<ProrationLine>(plan.Lines.Count);
        var explained = new List<object>();
        foreach (var planned in plan.Lines)
        {
            var magnitude = Math.Abs(planned.Unrounded);
            if (!cache.TryGetValue(magnitude, out var outcome))
            {
                outcome = await rounding.ApplyAsync(
                    new RoundingApplyRequest
                    {
                        Amount = new Money(magnitude, input.Currency),
                        Currency = input.Currency,
                        Purpose = RoundingPurpose,
                        Context = new RoundingApplyRequest.ContextDetail { LegalEntity = legalEntity, ValidAt = input.TermFrom },
                    },
                    cancellationToken).ConfigureAwait(false);
                cache[magnitude] = outcome;
            }

            var roundedMagnitude = outcome.AmountAfterRounding is { } rounded && rounded.Currency == input.Currency
                ? rounded.Amount
                : throw ProrationCalculator.Error("DATA-UNAVAILABLE", "MKT rounding returned no amount in the request currency; proration fails closed.");
            var ruleId = outcome.RuleKey ?? outcome.RuleId?.ToString()
                ?? throw ProrationCalculator.Error("DATA-UNAVAILABLE", "MKT rounding returned no rule id; proration fails closed.");

            var sign = (planned.Unrounded < 0m ? -1m : 1m) * (reverse[planned.Segment.SegmentId] ? -1m : 1m);
            var amount = roundedMagnitude == 0m ? 0m : sign * roundedMagnitude;
            var unrounded = reverse[planned.Segment.SegmentId] ? -planned.Unrounded : planned.Unrounded;
            var residual = unrounded - amount; // unrounded - rounded, same sign as the amount
            lines.Add(new ProrationLine
            {
                SegmentId = planned.Segment.SegmentId,
                ElementLocator = planned.Line.Element,
                CoverageCode = planned.Line.Coverage,
                ChargeType = planned.Line.ChargeType,
                ChargeCategory = planned.Line.Category,
                Period = DateRange.Of(planned.Segment.From, planned.Segment.To),
                Days = planned.Days,
                TermDays = plan.TermDays,
                Fraction = planned.Fraction,
                Amount = new Money(amount, input.Currency),
                Residual = new Money(residual, input.Currency),
                RoundingRuleId = ruleId,
            });
            explained.Add(new { planned.Segment.SegmentId, chargeType = planned.Line.ChargeType, unrounded, ruleId });
        }

        var total = Money.Sum(lines.Select(l => l.Amount), input.Currency);
        return new ProrationProrateResponse
        {
            Lines = lines,
            Total = total,
            Convention = declared,
            ConfigurationHash = request.ConfigurationHash,
            Explanation = provisional || request.Explain == true
                ? JsonSerializer.SerializeToElement(new
                {
                    convention = declared.ToString(),
                    conventionProvisional = provisional,
                    conventionNote = note,
                    termDays = plan.TermDays,
                    termMonths = plan.TermMonths,
                    orderOperations = request.OrderOperations,
                    lines = request.Explain == true ? explained : null,
                })
                : null,
        };
    }

    /// <summary>The convention the product artefact declares, whether it is provisional, and the artefact's note.</summary>
    private async Task<(DayCountConvention Convention, bool Provisional, string? Note)> DeclaredConventionAsync(ProrationProrateRequest request, CancellationToken cancellationToken)
    {
        JsonElement? artefact;
        try
        {
            artefact = (await products.GetAsync(request.ProductArtefactHash.Value, cancellationToken: cancellationToken).ConfigureAwait(false)).CanonicalJsonArtefact;
        }
        catch (DomainException)
        {
            throw ProrationCalculator.Error("INPUT", "The product artefact is unknown.");
        }

        if (artefact is not { ValueKind: JsonValueKind.Object } json
            || !json.TryGetProperty("dayCount", out var dayCount) || dayCount.ValueKind != JsonValueKind.String)
        {
            throw ProrationCalculator.Error("CONVENTION", "The product artefact declares no day-count convention (REQ-RAT-165).");
        }

        var declared = dayCount.GetString()?.Trim().ToUpperInvariant().Replace('/', '_') switch
        {
            "TERM_RATIO" => DayCountConvention.TermRatio,
            "ACT_365F" => DayCountConvention.Act365f,
            "ACT_ACT" => DayCountConvention.ActAct,
            "30E_360" or "THIRTY_E_360" => DayCountConvention.ThirtyE360,
            _ => throw ProrationCalculator.Error("CONVENTION", "The product artefact declares a day-count convention RAT does not know."),
        };
        var provisional = json.TryGetProperty("dayCountProvisional", out var flag) && flag.ValueKind == JsonValueKind.True;
        var note = json.TryGetProperty("dayCountNote", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        return (declared, provisional, note);
    }

    private static CalcInput Map(ProrationProrateRequest request)
    {
        if (request.Term.End is not { } termEnd || request.Periods.Any(p => p.Period.End is null))
        {
            throw ProrationCalculator.Error("PERIOD", "The term and every period must have an end.");
        }

        if (request.AnnualRates.Count == 0 || request.Periods.Count == 0)
        {
            throw ProrationCalculator.Error("INPUT", "Send at least one period and one annual rate.");
        }

        var currencies = request.AnnualRates.Select(r => r.AnnualAmount.Currency).Distinct().ToList();
        if (currencies.Count != 1)
        {
            throw ProrationCalculator.Error("CURRENCY", "All annual rates must be in one currency.");
        }

        return new CalcInput(
            currencies[0],
            request.Term.Start,
            termEnd,
            request.Convention,
            [.. request.Periods.Select(p => new CalcSegment(p.SegmentId, p.Period.Start, p.Period.End!.Value, p.Reverse == true))],
            [.. request.AnnualRates.Select(r => new CalcLine(r.SegmentId, r.ElementLocator, r.CoverageCode, r.ChargeType, r.ChargeCategory, r.AnnualAmount.Amount, r.Handling))]);
    }
}
