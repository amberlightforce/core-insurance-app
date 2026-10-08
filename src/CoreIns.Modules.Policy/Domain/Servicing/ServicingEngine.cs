using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Domain.Servicing;

/// <summary>
/// The pure servicing engine (REQ-POL-115/116/119/121/122/123/214; REQ-RAT-004): given the head's segments and an
/// intent it returns the new segment list and the NET charge deltas per element × coverage × charge type × valid
/// period. No I/O, no clock, decimal only.
/// <para>
/// Arithmetic. Every segment carries its written (rounded) amount and an unrounded exact value (<see cref="ServicingSegment.Exact"/>,
/// 20 decimals). Per charge key the written cumulative is kept equal to the rounding of the exact cumulative, so a delta is
/// <c>round(exactCumulativeAfter) − writtenCumulativeBefore</c>: rounding residuals never accumulate beyond one rounding
/// step however many changes there are (REQ-POL-123), and the residual stays with the elapsed part (REQ-POL-118).
/// A cancellation or change at <c>t</c> splits the segment containing <c>t</c>: the unearned share
/// <c>(F − f) ÷ F</c> moves on (F = the convention's fraction for the whole segment, f = the fraction elapsed), so under
/// ACT/365F a full-term segment earns <c>days ÷ 365</c> and unearned = written − earned. A Change debits
/// <c>(newRate − oldRate) × (1 − elapsedFraction)</c> with the same fractions, so Change and Cancel share one basis
/// (REQ-RAT-004). A flat or same-day cancel therefore credits exactly the written amount.
/// </para>
/// <para>
/// Callers must pass the <b>full</b> rate set on a <see cref="ChangeIntent"/>: a key missing from it means rate 0 and ends
/// that charge from the effective time. A mid-term change of a flat charge's rate, and any change of the Flat,
/// RefundableOnCancel or category attributes of an existing key, are refused as <see cref="ServicingRefusal.InvalidInput"/>.
/// A rounding rule that returns more decimals than the currency's minor units, or arithmetic overflow, is also a typed
/// refusal, never an exception.
/// </para>
/// </summary>
internal sealed class ServicingEngine(IProration proration, PremiumRounding rounding)
{
    /// <summary>Applies an intent. Never throws for business refusals; they come back typed.</summary>
    public ServicingResult Apply(ServicingState? state, ServicingIntent intent, DeltaCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(correlation);
        var currency = intent is NewTermIntent open ? open.Term.Currency : state?.Term.Currency;
        if (currency is null)
        {
            return ServicingResult.Refused(ServicingRefusal.InvalidInput, "A change or cancellation needs the term's current state.");
        }

        try
        {
            return new Run(proration, rounding, currency.Value).Apply(state, intent, correlation);
        }
        catch (Exception ex) when (ex is PrecisionLossException or OverflowException or DivideByZeroException or RoundingViolation)
        {
            return ServicingResult.Refused(ServicingRefusal.InvalidInput, ex.Message);
        }
    }

    private sealed class RoundingViolation(string message) : Exception(message);

    private sealed class Run(IProration proration, PremiumRounding rounding, Currency currency)
    {
        private const int DivisionScale = 20;

        public ServicingResult Apply(ServicingState? state, ServicingIntent intent, DeltaCorrelation correlation)
        {
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

        // ---- new term --------------------------------------------------------------------------------------------

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
            var full = proration.Fraction(term.Convention, termDays, termDays);
            var segments = new List<ServicingSegment>();
            var deltas = new List<ServicingDelta>();
            foreach (var rate in intent.Rates.OrderBy(r => r.Key))
            {
                var exact = rate.Flat ? rate.AnnualRate : Times(rate.AnnualRate, full.Numerator, full.Denominator);
                var amount = Round(exact);
                segments.Add(new ServicingSegment(rate, term.From, term.To, amount, exact));
                if (amount != 0m)
                {
                    deltas.Add(Delta(term, rate, term.From, term.To, amount, termDays, full, TransactionKind.NewBusiness, correlation, deltas.Count + 1));
                }
            }

            return ServicingResult.Accepted(new ServicingState(term, segments, null, term.From), deltas);
        }

        // ---- change ----------------------------------------------------------------------------------------------

        private ServicingResult Change(ServicingState state, ChangeIntent intent, DeltaCorrelation correlation)
        {
            var term = state.Term;
            var guard = Guard(state, intent.EffectiveAt, out var t);
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
            var remainingDays = DayCount.Days(t, term.To, term.Zone);
            var remaining = Remaining(term, t);
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
                if (last is not null && requested is not null
                    && (requested.Flat != last.Rate.Flat || requested.RefundableOnCancel != last.Rate.RefundableOnCancel || requested.ChargeCategory != last.Rate.ChargeCategory))
                {
                    return ServicingResult.Refused(ServicingRefusal.InvalidInput, $"{key.ChargeType}: Flat, RefundableOnCancel and category cannot change for an existing charge.");
                }

                var newRate = requested ?? last!.Rate with { AnnualRate = 0m };
                if (last is not null && last.Rate.AnnualRate == newRate.AnnualRate)
                {
                    segments.AddRange(existing!);
                    continue;
                }

                if (last is not null && last.Rate.Flat)
                {
                    return ServicingResult.Refused(ServicingRefusal.InvalidInput, $"{key.ChargeType}: a flat charge's rate cannot change mid-term (or be omitted from the rate set).");
                }

                var oldRate = last?.Rate.AnnualRate ?? 0m;
                var cumulativeBefore = existing?.Sum(s => s.Amount) ?? 0m;
                var exactBefore = existing?.Sum(ExactOf) ?? 0m;

                decimal unearnedAmount = 0m, unearnedExact = 0m;
                if (last is not null && !last.Rate.Flat)
                {
                    var (num, den) = UnearnedShare(last, t, term, termDays);
                    unearnedExact = Times(ExactOf(last), num, den);
                    unearnedAmount = Math.Clamp(Round(Times(last.Amount, num, den)), 0m, last.Amount);
                }

                decimal delta, deltaExact;
                if (newRate.Flat)
                {
                    deltaExact = newRate.AnnualRate;
                    delta = Round(deltaExact);
                }
                else if (newRate.AnnualRate == 0m)
                {
                    deltaExact = -unearnedExact;
                    delta = -unearnedAmount;
                }
                else
                {
                    deltaExact = Times(newRate.AnnualRate - oldRate, remaining.Numerator, remaining.Denominator);
                    delta = Math.Max(Round(exactBefore + deltaExact) - cumulativeBefore, -unearnedAmount);
                }

                if (existing is not null)
                {
                    segments.AddRange(existing.Take(existing.Count - 1));
                }

                var elapsedAmount = 0m;
                if (last is not null && t > last.From)
                {
                    elapsedAmount = last.Amount - unearnedAmount;
                    segments.Add(last with { To = t, Amount = elapsedAmount, Exact = ExactOf(last) - unearnedExact });
                }

                var carried = last is null ? 0m : last.Amount - elapsedAmount;
                var carriedExact = unearnedExact;
                segments.Add(new ServicingSegment(newRate, t, term.To, carried + delta, carriedExact + deltaExact));
                if (delta != 0m)
                {
                    var kind = delta > 0m ? TransactionKind.EndorsementDebit : TransactionKind.EndorsementCredit;
                    deltas.Add(Delta(term, newRate, t, term.To, delta, remainingDays, newRate.Flat ? new ProrationFraction(1, 1) : remaining, kind, correlation, deltas.Count + 1));
                }
            }

            var boundary = state.LatestBoundEffective is { } prior ? Instant.Max(prior, t) : t;
            return ServicingResult.Accepted(state with { Segments = segments, LatestBoundEffective = boundary }, deltas);
        }

        // ---- end cover -------------------------------------------------------------------------------------------

        private ServicingResult EndCover(ServicingState state, EndCoverIntent intent, DeltaCorrelation correlation)
        {
            var term = state.Term;
            if (!Enum.IsDefined(intent.RefundMethod))
            {
                return ServicingResult.Refused(ServicingRefusal.UnknownRefundMethod, $"Unknown refund method {intent.RefundMethod}.");
            }

            var guard = Guard(state, intent.EffectiveAt, out var t);
            if (guard is not null)
            {
                return guard;
            }

            var flatCancel = intent.RefundMethod == RefundMethod.FullRefund;
            if (flatCancel && DayCount.Date(t, term.Zone) != DayCount.Date(term.From, term.Zone))
            {
                return ServicingResult.Refused(ServicingRefusal.FullRefundNotFlat, "A full refund is only possible for a flat cancel on the term's first date.");
            }

            var termDays = term.Days;
            var segments = new List<ServicingSegment>();
            var deltas = new List<ServicingDelta>();
            var remainingDays = DayCount.Days(t, term.To, term.Zone);
            foreach (var group in state.Segments.GroupBy(s => s.Key).OrderBy(g => g.Key))
            {
                var list = group.OrderBy(s => s.From).ToList();
                var last = list[^1];
                if (last.From > t)
                {
                    return ServicingResult.Refused(ServicingRefusal.OutOfSequence, $"{last.Key.ChargeType} has a segment starting after the effective time.");
                }

                decimal credit;
                var elapsedExact = ExactOf(last);
                ProrationFraction fraction;
                if (last.Rate.Flat)
                {
                    credit = last.Rate.RefundableOnCancel ? list.Sum(s => s.Amount) : 0m;
                    fraction = new ProrationFraction(1, 1);
                    elapsedExact = last.Amount - credit;
                }
                else if (flatCancel)
                {
                    credit = list.Sum(s => s.Amount);
                    fraction = new ProrationFraction(1, 1);
                    elapsedExact = last.Amount - credit;
                }
                else
                {
                    var (num, den) = UnearnedShare(last, t, term, termDays);
                    var unearnedExact = Times(ExactOf(last), num, den);
                    var cumulative = list.Sum(s => s.Amount);
                    credit = Math.Clamp(cumulative - Round(list.Sum(ExactOf) - unearnedExact), 0m, last.Amount);
                    elapsedExact = ExactOf(last) - unearnedExact;
                    fraction = new ProrationFraction(checked((int)num), checked((int)den));
                }

                segments.AddRange(list.Take(list.Count - 1));
                var elapsedAmount = last.Amount - credit;
                if (t > last.From)
                {
                    segments.Add(last with { To = t, Amount = elapsedAmount, Exact = elapsedExact });
                }
                else if (elapsedAmount != 0m)
                {
                    // The last segment is dropped (cancelled at its start). Its remaining written amount (a non-refundable
                    // flat charge, say) stays with the previous segment of the same key, else in a zero-length segment.
                    var previousIndex = segments.FindLastIndex(s => s.Key == last.Key);
                    if (previousIndex >= 0)
                    {
                        segments[previousIndex] = segments[previousIndex] with
                        {
                            Amount = segments[previousIndex].Amount + elapsedAmount,
                            Exact = ExactOf(segments[previousIndex]) + elapsedExact,
                        };
                    }
                    else
                    {
                        segments.Add(last with { To = t, Amount = elapsedAmount, Exact = elapsedExact });
                    }
                }

                if (credit != 0m)
                {
                    deltas.Add(Delta(term, last.Rate, t, last.To, -credit, remainingDays, fraction, TransactionKind.Cancellation, correlation, deltas.Count + 1));
                }
            }

            return ServicingResult.Accepted(state with { Segments = segments, CoverEndedAt = t, LatestBoundEffective = t }, deltas);
        }

        // ---- shared ----------------------------------------------------------------------------------------------

        /// <summary>Guards the effective time. Dates, not instants, bound the term start (a same-date time before the term's start is clamped to it).</summary>
        private static ServicingResult? Guard(ServicingState state, Instant effective, out Instant t)
        {
            var term = state.Term;
            t = effective;
            if (state.CoverEndedAt is not null)
            {
                return ServicingResult.Refused(ServicingRefusal.AfterCancellation, "The cover of this term has ended.");
            }

            if (DayCount.Date(effective, term.Zone) < DayCount.Date(term.From, term.Zone) || effective >= term.To)
            {
                return ServicingResult.Refused(ServicingRefusal.EffectiveOutsideTerm, "The effective time is outside the term.");
            }

            if (effective < term.From)
            {
                t = term.From;
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

        private static decimal ExactOf(ServicingSegment segment) => segment.Exact ?? segment.Amount;

        /// <summary>The unearned share (F − f) ÷ F of a segment at <paramref name="t"/>, with F and f from the one shared proration.</summary>
        private (long Numerator, long Denominator) UnearnedShare(ServicingSegment segment, Instant t, ServicingTerm term, int termDays)
        {
            var segmentDays = DayCount.Days(segment.From, segment.To, term.Zone);
            var elapsedDays = Math.Clamp(DayCount.Days(segment.From, t, term.Zone), 0, Math.Max(segmentDays, 0));
            if (segmentDays <= 0)
            {
                return (0, 1);
            }

            var whole = proration.Fraction(term.Convention, segmentDays, termDays);
            var elapsed = proration.Fraction(term.Convention, elapsedDays, termDays);
            var g = Gcd(whole.Denominator, elapsed.Denominator);
            long num = ((long)whole.Numerator * (elapsed.Denominator / g)) - ((long)elapsed.Numerator * (whole.Denominator / g));
            long den = (long)whole.Numerator * (elapsed.Denominator / g);
            return den <= 0 ? (0, 1) : (Math.Clamp(num, 0, den), den);
        }

        /// <summary>The fraction of the term still to run from <paramref name="t"/>: 1 − (fraction elapsed since the term's start).</summary>
        private ProrationFraction Remaining(ServicingTerm term, Instant t)
        {
            var termDays = term.Days;
            var whole = proration.Fraction(term.Convention, termDays, termDays);
            var elapsed = proration.Fraction(term.Convention, DayCount.Days(term.From, t, term.Zone), termDays);
            var g = Gcd(whole.Denominator, elapsed.Denominator);
            var num = ((long)whole.Numerator * (elapsed.Denominator / g)) - ((long)elapsed.Numerator * (whole.Denominator / g));
            var den = (long)whole.Denominator * (elapsed.Denominator / g);
            return new ProrationFraction(checked((int)Math.Max(num, 0)), checked((int)den));
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }

            return a;
        }

        /// <summary>value × numerator ÷ denominator, exact to 20 decimals (toward zero: a tie can neither be created nor hidden at these operand sizes).</summary>
        private static decimal Times(decimal value, long numerator, long denominator) =>
            ExactDecimal.Divide(ExactDecimal.Multiply(value, numerator), denominator, DivisionScale, MidpointRounding.ToZero);

        private decimal Round(decimal amount)
        {
            var rounded = rounding(amount);
            if (decimal.Round(rounded, currency.MinorUnits, MidpointRounding.ToZero) != rounded)
            {
                throw new RoundingViolation($"The premium rounding rule returned {rounded}, which has more decimals than {currency.Code} allows ({currency.MinorUnits}).");
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
}
