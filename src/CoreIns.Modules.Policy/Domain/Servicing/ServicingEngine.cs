using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Domain.Servicing;

/// <summary>
/// The pure servicing engine (REQ-POL-115/116/119/121/122/123/214; REQ-RAT-004): given the head's segments and an
/// intent it returns the new segment list and the NET charge deltas per element × coverage × charge type × valid
/// period. No I/O, no clock, decimal only.
/// <para>
/// Arithmetic. A segment writes <c>round(annualRate × fraction)</c>. A cancellation credits the unearned share of the
/// segment's own written amount, <c>round(written × remainingDays ÷ segmentDays)</c>, so a flat or same-day cancel
/// credits exactly the written amount and earned + unearned = written (the residual stays with the elapsed part,
/// REQ-POL-118/123). A change at <c>t</c> debits or credits <c>round((newRate − oldRate) × remainingDays ÷ basis)</c>;
/// the unearned old amount moves to the new segment together with that delta, so Σ deltas = Σ segment amounts (P7).
/// Division is exact (20 decimals, toward zero: for these operands a tie can never be created or hidden by it), and
/// the single rounding is the caller's MKT premium rule.
/// </para>
/// </summary>
internal sealed class ServicingEngine(IProration proration, PremiumRounding rounding)
{
    private const int DivisionScale = 20;

    /// <summary>Applies an intent. Never throws for business refusals; they come back typed.</summary>
    public ServicingResult Apply(ServicingState? state, ServicingIntent intent, DeltaCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(correlation);
        if (intent is NewTermIntent open)
        {
            return OpenTerm(open, correlation);
        }

        if (state is null)
        {
            return ServicingResult.Refused(ServicingRefusal.InvalidInput, "A change or cancellation needs the term's current state.");
        }

        var invalid = Validate(state);
        if (invalid is not null)
        {
            return invalid;
        }

        return intent switch
        {
            ChangeIntent change => Change(state, change, correlation),
            EndCoverIntent end => EndCover(state, end, correlation),
            _ => ServicingResult.Refused(ServicingRefusal.InvalidInput, "Unknown intent."),
        };
    }

    // ---- new term ------------------------------------------------------------------------------------------------

    private ServicingResult OpenTerm(NewTermIntent intent, DeltaCorrelation correlation)
    {
        var term = intent.Term;
        if (term.To <= term.From || term.Days <= 0)
        {
            return ServicingResult.Refused(ServicingRefusal.InvalidInput, "The term must span at least one Athens calendar day.");
        }

        var rateError = ValidateRates(intent.Rates);
        if (rateError is not null)
        {
            return rateError;
        }

        var termDays = term.Days;
        var segments = new List<ServicingSegment>();
        var deltas = new List<ServicingDelta>();
        var fraction = proration.Fraction(term.Convention, termDays, termDays);
        foreach (var rate in intent.Rates.OrderBy(r => r.Key))
        {
            var amount = rate.Flat ? Round(rate.AnnualRate) : Scale(rate.AnnualRate, fraction);
            segments.Add(new ServicingSegment(rate, term.From, term.To, amount));
            if (amount != 0m)
            {
                deltas.Add(Delta(term, rate, term.From, term.To, amount, termDays, fraction, TransactionKind.NewBusiness, correlation, deltas.Count + 1));
            }
        }

        return ServicingResult.Accepted(new ServicingState(term, segments, null, term.From), deltas);
    }

    // ---- change --------------------------------------------------------------------------------------------------

    private ServicingResult Change(ServicingState state, ChangeIntent intent, DeltaCorrelation correlation)
    {
        var term = state.Term;
        var t = intent.EffectiveAt;
        var guard = Guard(state, t);
        if (guard is not null)
        {
            return guard;
        }

        var rateError = ValidateRates(intent.NewRates);
        if (rateError is not null)
        {
            return rateError;
        }

        var termDays = term.Days;
        var remaining = DayCount.Days(t, term.To, term.Zone);
        var fraction = proration.Fraction(term.Convention, remaining, termDays);
        var newRates = intent.NewRates.ToDictionary(r => r.Key);
        var byKey = state.Segments.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.OrderBy(s => s.From).ToList());
        var keys = byKey.Keys.Union(newRates.Keys).Order().ToList();

        var segments = new List<ServicingSegment>();
        var deltas = new List<ServicingDelta>();
        foreach (var key in keys)
        {
            byKey.TryGetValue(key, out var existing);
            var last = existing?[^1];
            if (last is not null && last.From > t)
            {
                return ServicingResult.Refused(ServicingRefusal.OutOfSequence, $"{key.ChargeType} has a segment starting after the effective time.");
            }

            newRates.TryGetValue(key, out var requested);
            var newRate = requested ?? last!.Rate with { AnnualRate = 0m };
            if (last is not null && last.Rate.AnnualRate == newRate.AnnualRate)
            {
                segments.AddRange(existing!);
                continue;
            }

            var oldRate = last?.Rate.AnnualRate ?? 0m;
            var unearned = 0m;
            if (last is not null && !last.Rate.Flat)
            {
                unearned = Unearned(last, t, term.Zone, out _, out _);
            }

            decimal delta;
            if (newRate.Flat)
            {
                delta = Round(newRate.AnnualRate - oldRate);
            }
            else if (newRate.AnnualRate == 0m)
            {
                delta = -unearned;
            }
            else
            {
                delta = Scale(newRate.AnnualRate - oldRate, fraction);
            }

            if (existing is not null)
            {
                segments.AddRange(existing.Take(existing.Count - 1));
            }

            var elapsedAmount = 0m;
            if (last is not null && t > last.From)
            {
                elapsedAmount = last.Amount - unearned;
                segments.Add(last with { To = t, Amount = elapsedAmount });
            }

            var carried = last is null ? 0m : last.Amount - elapsedAmount;
            segments.Add(new ServicingSegment(newRate, t, term.To, carried + delta));
            if (delta != 0m)
            {
                var kind = delta > 0m ? TransactionKind.EndorsementDebit : TransactionKind.EndorsementCredit;
                deltas.Add(Delta(term, newRate, t, term.To, delta, remaining, newRate.Flat ? new ProrationFraction(1, 1) : fraction, kind, correlation, deltas.Count + 1));
            }
        }

        var boundary = state.LatestBoundEffective is { } prior ? Instant.Max(prior, t) : t;
        return ServicingResult.Accepted(state with { Segments = segments, LatestBoundEffective = boundary }, deltas);
    }

    // ---- end cover -----------------------------------------------------------------------------------------------

    private ServicingResult EndCover(ServicingState state, EndCoverIntent intent, DeltaCorrelation correlation)
    {
        var term = state.Term;
        var t = intent.EffectiveAt;
        if (!Enum.IsDefined(intent.RefundMethod))
        {
            return ServicingResult.Refused(ServicingRefusal.UnknownRefundMethod, $"Unknown refund method {intent.RefundMethod}.");
        }

        var guard = Guard(state, t);
        if (guard is not null)
        {
            return guard;
        }

        var flatCancel = intent.RefundMethod == RefundMethod.FullRefund;
        if (flatCancel && DayCount.Date(t, term.Zone) != DayCount.Date(term.From, term.Zone))
        {
            return ServicingResult.Refused(ServicingRefusal.FullRefundNotFlat, "A full refund is only possible for a flat cancel on the term's first date.");
        }

        var segments = new List<ServicingSegment>();
        var deltas = new List<ServicingDelta>();
        var remaining = DayCount.Days(t, term.To, term.Zone);
        foreach (var group in state.Segments.GroupBy(s => s.Key).OrderBy(g => g.Key))
        {
            var list = group.OrderBy(s => s.From).ToList();
            var last = list[^1];
            if (last.From > t)
            {
                return ServicingResult.Refused(ServicingRefusal.OutOfSequence, $"{last.Key.ChargeType} has a segment starting after the effective time.");
            }

            decimal credit;
            ProrationFraction fraction;
            var segmentDays = DayCount.Days(last.From, last.To, term.Zone);
            if (last.Rate.Flat)
            {
                credit = last.Rate.RefundableOnCancel ? list.Sum(s => s.Amount) : 0m;
                fraction = new ProrationFraction(1, 1);
            }
            else if (flatCancel)
            {
                credit = list.Sum(s => s.Amount);
                fraction = new ProrationFraction(Math.Max(segmentDays, 1), Math.Max(segmentDays, 1));
            }
            else
            {
                var days = DayCount.Days(t, last.To, term.Zone);
                credit = Unearned(last, t, term.Zone, out _, out _);
                fraction = new ProrationFraction(days, Math.Max(segmentDays, 1));
            }

            segments.AddRange(list.Take(list.Count - 1));
            var elapsedAmount = last.Amount - credit;
            if (t > last.From)
            {
                segments.Add(last with { To = t, Amount = elapsedAmount });
            }
            else if (elapsedAmount != 0m && segments.Count > 0)
            {
                // Dropped zero-length segment: its residual (flat refundable only) stays with the previous segment.
                var previousIndex = segments.FindLastIndex(s => s.Key == last.Key);
                segments[previousIndex] = segments[previousIndex] with { Amount = segments[previousIndex].Amount + elapsedAmount };
            }

            if (credit != 0m)
            {
                deltas.Add(Delta(term, last.Rate, t, last.To, -credit, remaining, fraction, TransactionKind.Cancellation, correlation, deltas.Count + 1));
            }
        }

        return ServicingResult.Accepted(state with { Segments = segments, CoverEndedAt = t, LatestBoundEffective = t }, deltas);
    }

    // ---- shared --------------------------------------------------------------------------------------------------

    private static ServicingResult? Guard(ServicingState state, Instant t)
    {
        if (state.CoverEndedAt is not null)
        {
            return ServicingResult.Refused(ServicingRefusal.AfterCancellation, "The cover of this term has ended.");
        }

        if (t < state.Term.From || t >= state.Term.To)
        {
            return ServicingResult.Refused(ServicingRefusal.EffectiveOutsideTerm, "The effective time is outside the term.");
        }

        if (state.LatestBoundEffective is { } latest && t < latest)
        {
            return ServicingResult.Refused(ServicingRefusal.OutOfSequence, "The effective time is earlier than the latest bound transaction (D-SL3-02).");
        }

        return null;
    }

    private static ServicingResult? Validate(ServicingState state)
    {
        var term = state.Term;
        if (term.To <= term.From)
        {
            return ServicingResult.Refused(ServicingRefusal.InvalidInput, "The term has no duration.");
        }

        foreach (var group in state.Segments.GroupBy(s => s.Key))
        {
            var list = group.OrderBy(s => s.From).ToList();
            if (list[0].From < term.From || list[^1].To != (state.CoverEndedAt ?? term.To))
            {
                return ServicingResult.Refused(ServicingRefusal.InvalidInput, $"Segments of {group.Key.ChargeType} do not end at the end of cover.");
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].To < list[i].From || (i > 0 && list[i].From != list[i - 1].To))
                {
                    return ServicingResult.Refused(ServicingRefusal.InvalidInput, $"Segments of {group.Key.ChargeType} are not contiguous.");
                }
            }
        }

        return null;
    }

    private static ServicingResult? ValidateRates(IReadOnlyList<ChargeRate> rates)
    {
        if (rates.Select(r => r.Key).Distinct().Count() != rates.Count)
        {
            return ServicingResult.Refused(ServicingRefusal.InvalidInput, "Duplicate element × coverage × charge type in the rates.");
        }

        return rates.Any(r => r.AnnualRate < 0m)
            ? ServicingResult.Refused(ServicingRefusal.InvalidInput, "Annual rates cannot be negative.")
            : null;
    }

    /// <summary>The unearned share of the segment's own written amount from <paramref name="t"/>: round(written × days ÷ segmentDays).</summary>
    private decimal Unearned(ServicingSegment segment, Instant t, TimeZoneInfo zone, out int days, out int segmentDays)
    {
        days = DayCount.Days(t, segment.To, zone);
        segmentDays = DayCount.Days(segment.From, segment.To, zone);
        if (segmentDays <= 0 || days <= 0)
        {
            return 0m;
        }

        if (days >= segmentDays)
        {
            return segment.Amount;
        }

        return Round(ExactDecimal.Divide(ExactDecimal.Multiply(segment.Amount, days), segmentDays, DivisionScale, MidpointRounding.ToZero));
    }

    private decimal Scale(decimal rate, ProrationFraction fraction) =>
        Round(ExactDecimal.Divide(ExactDecimal.Multiply(rate, fraction.Numerator), fraction.Denominator, DivisionScale, MidpointRounding.ToZero));

    private decimal Round(decimal amount)
    {
        var rounded = rounding(amount);
        if (decimal.Round(rounded, 4, MidpointRounding.ToZero) != rounded)
        {
            throw new InvalidOperationException($"The premium rounding rule returned {rounded}, which has more than 4 decimal places.");
        }

        return rounded;
    }

    private static ServicingDelta Delta(
        ServicingTerm term,
        ChargeRate rate,
        Instant from,
        Instant to,
        decimal amount,
        int days,
        ProrationFraction fraction,
        TransactionKind kind,
        DeltaCorrelation correlation,
        int sequence) =>
        new(
            rate.Key,
            rate.ChargeCategory,
            from,
            to,
            DayCount.Date(from, term.Zone),
            DayCount.Date(to, term.Zone),
            amount,
            days,
            fraction.Numerator,
            fraction.Denominator,
            kind,
            correlation.CorrelationId,
            correlation.SetId,
            sequence);
}
