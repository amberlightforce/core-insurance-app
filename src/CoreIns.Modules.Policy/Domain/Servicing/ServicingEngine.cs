using CoreIns.SharedKernel;

namespace CoreIns.Modules.Policy.Domain.Servicing;

/// <summary>
/// The pure servicing engine (REQ-POL-115/116/119/121/122/123/214; REQ-RAT-004): given the head's segments and an
/// intent it returns the new segment list and the NET charge deltas per element × coverage × charge type × valid
/// period. No I/O, no clock, decimal only.
/// <para>
/// Arithmetic. The exact cumulative of a charge key is a pure function of its segments' rates and periods and the
/// convention: Σ rate × (elapsed(to) − elapsed(from)), with elapsed from <see cref="IProration"/> (0 at term start, the
/// full-term fraction at term end), computed to 20 decimals. The written cumulative is always the single MKT rounding of
/// that exact value, so a delta is <c>round(exactAfter) − writtenBefore</c>, rounding residuals never accumulate beyond one
/// rounding step however many changes there are (REQ-POL-123), and earned (the rounding up to t) + unearned (the rest) =
/// written (REQ-POL-118). Change and Cancel share this one basis (REQ-RAT-004); a flat cancel has nothing earned and so
/// credits exactly the written amount. State is therefore only rates, periods and amounts: nothing else needs persisting.
/// Segment amounts that are not what the engine would have written (not equal to the rounding of the derived exact
/// cumulative, or negative) are refused as <see cref="ServicingRefusal.InvalidInput"/>; no clamp hides corrupt input.
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
                segments.Add(new ServicingSegment(rate, term.From, term.To, amount));
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

            var inconsistent = ValidateAmounts(state);
            if (inconsistent is not null)
            {
                return inconsistent;
            }

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

                var cumulativeBefore = existing?.Sum(s => s.Amount) ?? 0m;
                var priors = existing is null ? [] : existing.Take(existing.Count - 1).ToList();
                var priorSum = priors.Sum(s => s.Amount);

                decimal cumulativeAfter, newAmount, elapsedAmount = 0m;
                if (newRate.Flat)
                {
                    cumulativeAfter = Round(newRate.AnnualRate);
                    newAmount = cumulativeAfter;
                }
                else
                {
                    // The key's written cumulative is always the rounding of the exact cumulative, a pure function of the
                    // segments' rates and periods: residuals are carried and nothing beyond the segments needs persisting.
                    var exactUpTo = ExactTo(existing, t, term);
                    cumulativeAfter = Round(exactUpTo + Piece(newRate.AnnualRate, t, term.To, term));
                    var elapsedSegment = last is not null && t > last.From;
                    elapsedAmount = elapsedSegment ? Round(exactUpTo) - priorSum : 0m;
                    newAmount = cumulativeAfter - (priorSum + elapsedAmount);
                    if (elapsedAmount < 0m || newAmount < 0m)
                    {
                        return ServicingResult.Refused(ServicingRefusal.InvalidInput, $"{key.ChargeType}: the segments are inconsistent with the rounding rule (negative amount).");
                    }
                }

                var delta = cumulativeAfter - cumulativeBefore;
                segments.AddRange(priors);
                if (last is not null && t > last.From)
                {
                    segments.Add(last with { To = t, Amount = elapsedAmount });
                }

                segments.Add(new ServicingSegment(newRate, t, term.To, newAmount));
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

            if (intent.RefundMethod == RefundMethod.FullRefund && DayCount.Date(t, term.Zone) != DayCount.Date(term.From, term.Zone))
            {
                return ServicingResult.Refused(ServicingRefusal.FullRefundNotFlat, "A full refund is only possible for a flat cancel on the term's first date.");
            }

            var inconsistent = ValidateAmounts(state);
            if (inconsistent is not null)
            {
                return inconsistent;
            }

            var segments = new List<ServicingSegment>();
            var deltas = new List<ServicingDelta>();
            var remainingDays = DayCount.Days(t, term.To, term.Zone);
            var remaining = Remaining(term, t);
            foreach (var group in state.Segments.GroupBy(s => s.Key).OrderBy(g => g.Key))
            {
                var list = group.OrderBy(s => s.From).ToList();
                var last = list[^1];
                if (last.From > t)
                {
                    return ServicingResult.Refused(ServicingRefusal.OutOfSequence, $"{last.Key.ChargeType} has a segment starting after the effective time.");
                }

                var cumulative = list.Sum(s => s.Amount);
                var priorSum = cumulative - last.Amount;
                decimal credit, elapsedAmount;
                if (last.Rate.Flat)
                {
                    credit = last.Rate.RefundableOnCancel ? cumulative : 0m;
                    elapsedAmount = last.Amount - credit;
                }
                else
                {
                    // Earned = round(exact cumulative up to t); unearned = written - earned (REQ-POL-118). A flat cancel
                    // (t on the first date) has nothing earned, so it credits exactly the written amount.
                    elapsedAmount = Round(ExactTo(list, t, term)) - priorSum;
                    credit = last.Amount - elapsedAmount;
                    if (elapsedAmount < 0m || credit < 0m)
                    {
                        return ServicingResult.Refused(ServicingRefusal.InvalidInput, $"{last.Key.ChargeType}: the segments are inconsistent with the rounding rule (negative amount).");
                    }
                }

                segments.AddRange(list.Take(list.Count - 1));
                if (t > last.From)
                {
                    segments.Add(last with { To = t, Amount = elapsedAmount });
                }
                else if (elapsedAmount != 0m)
                {
                    // The last segment is dropped (cancelled at its start). Its remaining written amount (a non-refundable
                    // flat charge, say) stays with the previous segment of the same key, else in a zero-length segment.
                    var previousIndex = segments.FindLastIndex(s => s.Key == last.Key);
                    if (previousIndex >= 0)
                    {
                        segments[previousIndex] = segments[previousIndex] with { Amount = segments[previousIndex].Amount + elapsedAmount };
                    }
                    else
                    {
                        segments.Add(last with { To = t, Amount = elapsedAmount });
                    }
                }

                if (credit != 0m)
                {
                    deltas.Add(Delta(term, last.Rate, t, last.To, -credit, remainingDays, last.Rate.Flat ? new ProrationFraction(1, 1) : remaining, TransactionKind.Cancellation, correlation, deltas.Count + 1));
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

        /// <summary>The fraction of the term elapsed at <paramref name="t"/> (0 at the start, the full-term fraction at or after the end).</summary>
        private ProrationFraction Elapsed(ServicingTerm term, Instant t)
        {
            var termDays = term.Days;
            var days = t >= term.To ? termDays : Math.Clamp(DayCount.Days(term.From, t, term.Zone), 0, termDays);
            return proration.Fraction(term.Convention, days, termDays);
        }

        /// <summary>rate × (elapsed(b) − elapsed(a)): the exact amount an annual rate earns over [a, b).</summary>
        private decimal Piece(decimal rate, Instant a, Instant b, ServicingTerm term)
        {
            if (b <= a)
            {
                return 0m;
            }

            var ga = Elapsed(term, a);
            var gb = Elapsed(term, b);
            var num = ((long)gb.Numerator * ga.Denominator) - ((long)ga.Numerator * gb.Denominator);
            var den = (long)gb.Denominator * ga.Denominator;
            return num <= 0 ? 0m : Times(rate, num, den);
        }

        /// <summary>The exact (unrounded) cumulative of a key's segments earned up to <paramref name="cut"/>: Σ rate × Δelapsed.</summary>
        private decimal ExactTo(IReadOnlyList<ServicingSegment>? segments, Instant cut, ServicingTerm term) =>
            segments is null ? 0m : segments.Sum(s => Piece(s.Rate.AnnualRate, s.From, Instant.Min(s.To, cut), term));

        /// <summary>
        /// Fail closed on state the engine could not have produced: per non-flat key, the written cumulative must be the
        /// rounding of the exact cumulative derived from the segments' rates and periods, with no negative segment.
        /// </summary>
        private ServicingResult? ValidateAmounts(ServicingState state)
        {
            foreach (var group in state.Segments.GroupBy(s => s.Key))
            {
                var list = group.ToList();
                if (list.Any(s => s.Amount < 0m && !s.Rate.Flat))
                {
                    return ServicingResult.Refused(ServicingRefusal.InvalidInput, $"{group.Key.ChargeType}: a segment has a negative amount.");
                }

                if (!list[0].Rate.Flat && list.Sum(s => s.Amount) != Round(ExactTo(list, state.Term.To, state.Term)))
                {
                    return ServicingResult.Refused(ServicingRefusal.InvalidInput, $"{group.Key.ChargeType}: the written amounts do not match the segments' rates and periods.");
                }
            }

            return null;
        }

        /// <summary>The fraction of the term still to run from <paramref name="t"/>: full − elapsed.</summary>
        private ProrationFraction Remaining(ServicingTerm term, Instant t)
        {
            var whole = Elapsed(term, term.To);
            var elapsed = Elapsed(term, t);
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
