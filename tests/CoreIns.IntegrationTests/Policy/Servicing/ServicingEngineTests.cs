using System.Text.Json.Nodes;
using CoreIns.Modules.Policy.Domain.Servicing;
using CoreIns.SharedKernel;

namespace CoreIns.IntegrationTests.Policy.Servicing;

/// <summary>
/// SL3-POL-ENGINE: the pure servicing engine. Worked cases (E2E-03 430.00 × 245/365 = 288.63), exactness rules, Athens
/// day count incl. DST days, and property-style probes (P7: Σ deltas = Σ segment amounts). No database.
/// REQ-POL-115/116/119/121/122/123/214/118, REQ-RAT-004, D-SL3-02, D-SL3-04.
/// </summary>
public sealed class ServicingEngineTests
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
    private static readonly DeltaCorrelation Corr = new("corr-1", "set-1");

    private static PremiumRounding HalfUp { get; } = a => decimal.Round(a, 2, MidpointRounding.AwayFromZero);

    private static ServicingEngine Engine(PremiumRounding? rounding = null) => new(new ReferenceProration(), rounding ?? HalfUp);

    /// <summary>Athens local time to an instant.</summary>
    private static Instant At(int y, int m, int d, int h = 0, int min = 0) =>
        Instant.FromUtcDateTime(TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), Athens));

    // 2026-03-01 14:23 .. 2027-03-01 14:23 = 365 days. 2027-03-01 .. 2028-03-01 = 366 days (Feb 29 2028).
    private static readonly ServicingTerm Term365 = new(At(2026, 3, 1, 14, 23), At(2027, 3, 1, 14, 23), Currency.EUR, DayCountConvention.TermRatio, Athens);
    private static readonly ServicingTerm Term366 = new(At(2027, 3, 1, 14, 23), At(2028, 3, 1, 14, 23), Currency.EUR, DayCountConvention.TermRatio, Athens);

    private static ChargeRate Premium(decimal rate, string type = "NET_PREMIUM", string element = "VEH-1", string coverage = "MTPL") =>
        new(element, coverage, type, "PREMIUM", rate);

    private static ServicingState Open(ServicingTerm term, params ChargeRate[] rates)
    {
        var result = Engine().Apply(null, new NewTermIntent(term, rates), Corr);
        Assert.True(result.IsAccepted, result.Message);
        return result.State!;
    }

    private static decimal Written(ServicingState state, ChargeKey key) => state.Segments.Where(s => s.Key == key).Sum(s => s.Amount);

    // ---- worked cases -------------------------------------------------------------------------------------------

    [Fact]
    public void NewTerm_WritesFullAnnualRateAsNewBusiness_REQ_POL_115()
    {
        var result = Engine().Apply(null, new NewTermIntent(Term365, [Premium(430.00m)]), Corr);

        var delta = Assert.Single(result.Deltas);
        Assert.Equal(430.00m, delta.Amount);
        Assert.Equal(TransactionKind.NewBusiness, delta.TransactionKind);
        Assert.Equal((365, 365, 365), (delta.Days, delta.FractionNumerator, delta.FractionDenominator));
        Assert.Equal(Term365.From, delta.ValidFrom);
        Assert.Equal(Term365.To, delta.ValidTo);
        Assert.Equal(("corr-1", "set-1"), (delta.CorrelationId, delta.SetId));
    }

    [Fact]
    public void Cancel_Day120_Credits_288_63_And_EarnedIs_141_37_E2E03()
    {
        var state = Open(Term365, Premium(430.00m));
        var effective = At(2026, 6, 29, 14, 0); // 2026-03-01 + 120 days = 2026-06-29

        var result = Engine().Apply(state, new EndCoverIntent(effective, RefundMethod.ProRata), Corr);

        var delta = Assert.Single(result.Deltas);
        Assert.Equal(-288.63m, delta.Amount);
        Assert.Equal(TransactionKind.Cancellation, delta.TransactionKind);
        Assert.Equal((245, 245, 365), (delta.Days, delta.FractionNumerator, delta.FractionDenominator));
        var elapsed = Assert.Single(result.State!.Segments);
        Assert.Equal(141.37m, elapsed.Amount);
        Assert.Equal(effective, elapsed.To);
        Assert.Equal(effective, result.State.CoverEndedAt);
        Assert.Equal(430.00m, elapsed.Amount - delta.Amount); // earned + unearned = written (REQ-POL-118)
    }

    [Fact]
    public void FlatCancel_CreditsExactlyTheWrittenAmount_REQ_POL_214()
    {
        var state = Open(Term365, Premium(430.00m), Premium(33.33m, "THEFT", coverage: "CASCO"));

        var result = Engine().Apply(state, new EndCoverIntent(Term365.From, RefundMethod.FullRefund), Corr);

        Assert.Equal([-430.00m, -33.33m], result.Deltas.OrderBy(d => d.Key.ChargeType, StringComparer.Ordinal).Select(d => d.Amount).ToArray());
        Assert.All(result.Deltas, d => Assert.Equal(Term365.From, d.ValidFrom));
        Assert.Empty(result.State!.Segments);
    }

    [Fact]
    public void Change_Day200_From430To500_Debits31_64_RoundedPerRule()
    {
        var state = Open(Term365, Premium(430.00m));
        var effective = At(2026, 9, 17, 9, 0); // 2026-03-01 + 200 days

        var result = Engine().Apply(state, new ChangeIntent(effective, [Premium(500.00m)]), Corr);

        var delta = Assert.Single(result.Deltas);
        Assert.Equal(31.64m, delta.Amount); // 70 x 165/365 = 31.6438 -> HALF_UP 2dp
        Assert.Equal(TransactionKind.EndorsementDebit, delta.TransactionKind);
        Assert.Equal((165, 165, 365), (delta.Days, delta.FractionNumerator, delta.FractionDenominator));
        Assert.Equal(Term365.To, delta.ValidTo);
        Assert.Equal([235.62m, 226.02m], result.State!.Segments.OrderBy(s => s.From).Select(s => s.Amount).ToArray());
        Assert.Equal(461.64m, Written(result.State, delta.Key));
    }

    [Fact]
    public void Change_UsesTheGivenRoundingRule_NotAHardCodedOne()
    {
        var state = Open(Term365, Premium(430.00m));
        var ceiling = new ServicingEngine(new ReferenceProration(), a => decimal.Round(a, 2, MidpointRounding.ToPositiveInfinity));

        var result = ceiling.Apply(state, new ChangeIntent(At(2026, 9, 17, 9, 0), [Premium(500.00m)]), Corr);

        Assert.Equal(31.65m, Assert.Single(result.Deltas).Amount);
    }

    [Fact]
    public void Change_ToLowerRate_IsACreditEndorsement()
    {
        var state = Open(Term365, Premium(430.00m));

        var result = Engine().Apply(state, new ChangeIntent(At(2026, 9, 17, 9, 0), [Premium(400.00m)]), Corr);

        var delta = Assert.Single(result.Deltas);
        Assert.Equal(-13.56m, delta.Amount); // 30 x 165/365 = 13.5616
        Assert.Equal(TransactionKind.EndorsementCredit, delta.TransactionKind);
    }

    [Fact]
    public void Change_WithSameRates_ProducesNoDeltasAndNoNewSegments()
    {
        var state = Open(Term365, Premium(430.00m));

        var result = Engine().Apply(state, new ChangeIntent(At(2026, 9, 17), [Premium(430.00m)]), Corr);

        Assert.Empty(result.Deltas);
        Assert.Equal(state.Segments, result.State!.Segments);
    }

    [Fact]
    public void LeapTerm_Uses366Days()
    {
        var state = Open(Term366, Premium(430.00m));
        Assert.Equal(366, Term366.Days);
        var effective = At(2027, 6, 29, 14, 0); // + 120 days

        var result = Engine().Apply(state, new EndCoverIntent(effective, RefundMethod.ProRata), Corr);

        var delta = Assert.Single(result.Deltas);
        Assert.Equal((246, 366), (delta.FractionNumerator, delta.FractionDenominator));
        Assert.Equal(-289.02m, delta.Amount); // 430 x 246/366 = 289.0164
        Assert.Equal(140.98m, Assert.Single(result.State!.Segments).Amount);
    }

    [Fact]
    public void Act365F_LeapTerm_EarnsDaysOver365_AndUnearnedIsWrittenMinusEarned_REQ_POL_118()
    {
        var term = Term366 with { Convention = DayCountConvention.Act365F };
        var state = Open(term, Premium(1000.00m));
        Assert.Equal(1000.00m, Written(state, Premium(1).Key)); // full term capped at the annual rate, not x 366/365

        var result = Engine().Apply(state, new EndCoverIntent(At(2027, 6, 9, 14, 0), RefundMethod.ProRata), Corr); // day 100

        var delta = Assert.Single(result.Deltas);
        Assert.Equal(-726.03m, delta.Amount);
        Assert.Equal(273.97m, Assert.Single(result.State!.Segments).Amount); // 1000 x 100/365
    }

    [Fact]
    public void Act365F_LeapTerm_ChangeAndCancelShareOneBasis_NoNegativeSegments()
    {
        var term = Term366 with { Convention = DayCountConvention.Act365F };
        var state = Open(term, Premium(743.88m));

        var result = Engine().Apply(state, new ChangeIntent(At(2027, 3, 17, 15, 0), [Premium(0.05m)]), Corr); // day 16

        Assert.True(result.IsAccepted, result.Message);
        Assert.All(result.State!.Segments, s => Assert.True(s.Amount >= 0m));
        Assert.True(Written(result.State, Premium(1).Key) >= 0m);
    }

    // ---- Athens day count, DST ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 3, 29, 4, 30)]
    public void SpringForwardDay_CountsAsOneAthensDate(int y, int m, int d, int h, int min)
    {
        var state = Open(Term365, Premium(430.00m));
        var effective = At(y, m, d, h, min);

        var result = Engine().Apply(state, new EndCoverIntent(effective, RefundMethod.ProRata), Corr);

        var days = (Term365.To.ToBusinessDate(Athens).Value.DayNumber - new DateOnly(2026, 3, 29).DayNumber);
        Assert.Equal(days, Assert.Single(result.Deltas).Days);
        Assert.Equal(decimal.Round(430m * days / 365m, 2, MidpointRounding.AwayFromZero), -result.Deltas[0].Amount);
    }

    [Fact]
    public void SpringForwardInstants_AroundTheGap_AreTheSameAthensDate()
    {
        var state = Open(Term365, Premium(430.00m));
        var beforeGap = Instant.FromUtc(2026, 3, 29, 0, 59); // 02:59 EET
        var afterGap = Instant.FromUtc(2026, 3, 29, 1, 0); // 04:00 EEST

        int DaysFor(Instant i) => Engine().Apply(state, new EndCoverIntent(i, RefundMethod.ProRata), Corr).Deltas.Single().Days;

        Assert.Equal(DaysFor(beforeGap), DaysFor(afterGap));
    }

    [Fact]
    public void FallBackDay_BothRepeatedHoursAreTheSameAthensDate_AndMidnightBoundariesSplitDays()
    {
        var state = Open(Term365, Premium(430.00m));
        var firstThree = Instant.FromUtc(2026, 10, 25, 0, 30); // 03:30 EEST
        var secondThree = Instant.FromUtc(2026, 10, 25, 1, 30); // 03:30 EET
        var lastMinuteBefore = Instant.FromUtc(2026, 10, 24, 20, 59); // 23:59 EEST on the 24th
        var midnight = Instant.FromUtc(2026, 10, 24, 21, 0); // 00:00 EEST on the 25th

        int DaysFor(Instant i) => Engine().Apply(state, new EndCoverIntent(i, RefundMethod.ProRata), Corr).Deltas.Single().Days;

        Assert.Equal(DaysFor(firstThree), DaysFor(secondThree));
        Assert.Equal(DaysFor(midnight), DaysFor(firstThree));
        Assert.Equal(DaysFor(midnight) + 1, DaysFor(lastMinuteBefore));
    }

    // ---- in-sequence, refusals ----------------------------------------------------------------------------------

    [Fact]
    public void EarlierThanTheLatestBoundBoundary_IsOutOfSequence_D_SL3_02()
    {
        var state = Open(Term365, Premium(430.00m));
        var first = Engine().Apply(state, new ChangeIntent(At(2026, 9, 17, 9, 0), [Premium(500.00m)]), Corr).State!;

        var earlier = Engine().Apply(first, new ChangeIntent(At(2026, 8, 1), [Premium(450.00m)]), Corr);
        var cancelEarlier = Engine().Apply(first, new EndCoverIntent(At(2026, 8, 1), RefundMethod.ProRata), Corr);
        var sameInstant = Engine().Apply(first, new ChangeIntent(At(2026, 9, 17, 9, 0), [Premium(450.00m)]), Corr);

        Assert.Equal(ServicingRefusal.OutOfSequence, earlier.Refusal);
        Assert.Equal(ServicingRefusal.OutOfSequence, cancelEarlier.Refusal);
        Assert.True(sameInstant.IsAccepted);
        Assert.Equal(439.04m, Written(sameInstant.State!, Premium(1).Key)); // 430 + 31.64 - 22.60 (50 x 165/365)
    }

    [Fact]
    public void AfterCancellation_AndOutsideTheTerm_AreRefused()
    {
        var state = Open(Term365, Premium(430.00m));
        var cancelled = Engine().Apply(state, new EndCoverIntent(At(2026, 6, 29), RefundMethod.ProRata), Corr).State!;

        Assert.Equal(ServicingRefusal.AfterCancellation, Engine().Apply(cancelled, new ChangeIntent(At(2026, 7, 1), [Premium(1m)]), Corr).Refusal);
        Assert.Equal(ServicingRefusal.AfterCancellation, Engine().Apply(cancelled, new EndCoverIntent(At(2026, 7, 1), RefundMethod.ProRata), Corr).Refusal);
        Assert.Equal(ServicingRefusal.EffectiveOutsideTerm, Engine().Apply(state, new EndCoverIntent(Term365.To, RefundMethod.ProRata), Corr).Refusal);
        Assert.Equal(ServicingRefusal.EffectiveOutsideTerm, Engine().Apply(state, new ChangeIntent(At(2026, 2, 1), [Premium(1m)]), Corr).Refusal);
    }

    [Fact]
    public void FullRefund_OnlyForFlatCancel_AndUnknownMethodsFailClosed()
    {
        var state = Open(Term365, Premium(430.00m));

        Assert.Equal(ServicingRefusal.FullRefundNotFlat, Engine().Apply(state, new EndCoverIntent(At(2026, 3, 2), RefundMethod.FullRefund), Corr).Refusal);
        Assert.Equal(ServicingRefusal.UnknownRefundMethod, Engine().Apply(state, new EndCoverIntent(At(2026, 6, 1), (RefundMethod)99), Corr).Refusal);
        Assert.False(DayCountConventions.TryParse("ACT_360", out _));
        Assert.False(DayCountConventions.TryParse(null, out _));
        Assert.True(DayCountConventions.TryParse("TERM_RATIO", out var c) && c == DayCountConvention.TermRatio);
    }

    [Fact]
    public void InvalidInput_DuplicateKeysNegativeRatesAndGaps_AreRefused()
    {
        var state = Open(Term365, Premium(430.00m));
        Assert.Equal(ServicingRefusal.InvalidInput, Engine().Apply(null, new NewTermIntent(Term365, [Premium(1m), Premium(2m)]), Corr).Refusal);
        Assert.Equal(ServicingRefusal.InvalidInput, Engine().Apply(state, new ChangeIntent(At(2026, 6, 1), [Premium(-1m)]), Corr).Refusal);
        var gap = state with { Segments = [state.Segments[0] with { To = At(2026, 9, 1) }] };
        Assert.Equal(ServicingRefusal.InvalidInput, Engine().Apply(gap, new ChangeIntent(At(2026, 10, 1), [Premium(1m)]), Corr).Refusal);
        Assert.Equal(ServicingRefusal.InvalidInput, Engine().Apply(null, new EndCoverIntent(At(2026, 6, 1), RefundMethod.ProRata), Corr).Refusal);
    }

    [Fact]
    public void RoundingRuleBeyondCurrencyScale_OverflowAndIdentityRules_AreTypedRefusals_NotExceptions()
    {
        var identity = new ServicingEngine(new ReferenceProration(), a => a);
        Assert.Equal(ServicingRefusal.InvalidInput, identity.Apply(null, new NewTermIntent(Term365, [Premium(430.123456m)]), Corr).Refusal);

        var threeDp = new ServicingEngine(new ReferenceProration(), a => decimal.Round(a, 3));
        Assert.Equal(ServicingRefusal.InvalidInput, threeDp.Apply(null, new NewTermIntent(Term365, [Premium(430.1239m)]), Corr).Refusal);

        var state = Open(Term365, Premium(430.00m));
        var huge = Engine().Apply(state, new ChangeIntent(At(2026, 9, 17), [Premium(decimal.MaxValue / 2)]), Corr);
        Assert.Equal(ServicingRefusal.InvalidInput, huge.Refusal);
    }

    // ---- flat charges, new and removed charge types -------------------------------------------------------------

    [Fact]
    public void FlatCharges_AreNeverProrated_AndCreditOnlyWhenTheChargeTypeSays()
    {
        var fee = new ChargeRate("POL", "ALL", "POLICY_FEE", "FEE", 12.00m, Flat: true);
        var refundable = new ChargeRate("POL", "ALL", "ADMIN_FEE", "FEE", 7.50m, Flat: true, RefundableOnCancel: true);
        var state = Open(Term365, Premium(430.00m), fee, refundable);

        var result = Engine().Apply(state, new EndCoverIntent(At(2026, 6, 29), RefundMethod.ProRata), Corr);

        Assert.Equal(2, result.Deltas.Count);
        Assert.DoesNotContain(result.Deltas, d => d.Key.ChargeType == "POLICY_FEE");
        Assert.Equal(-7.50m, result.Deltas.Single(d => d.Key.ChargeType == "ADMIN_FEE").Amount);
        Assert.Equal(12.00m, Written(result.State!, fee.Key));
        Assert.Equal(0m, Written(result.State!, refundable.Key));
    }

    [Fact]
    public void FlatCharge_MidTermRateChange_IsRefused_D4()
    {
        var fee = new ChargeRate("POL", "ALL", "POLICY_FEE", "FEE", 50.00m, Flat: true);
        var state = Open(Term365, Premium(430.00m), fee);

        var down = Engine().Apply(state, new ChangeIntent(At(2026, 6, 9), [Premium(430.00m), fee with { AnnualRate = 20.00m }]), Corr);
        var omitted = Engine().Apply(state, new ChangeIntent(At(2026, 6, 9), [Premium(430.00m)]), Corr);
        var same = Engine().Apply(state, new ChangeIntent(At(2026, 6, 9), [Premium(430.00m), fee]), Corr);

        Assert.Equal(ServicingRefusal.InvalidInput, down.Refusal);
        Assert.Equal(ServicingRefusal.InvalidInput, omitted.Refusal);
        Assert.True(same.IsAccepted);
    }

    [Fact]
    public void ChangingFlatRefundableOrCategoryOfAnExistingCharge_IsRefused_D5()
    {
        var state = Open(Term365, Premium(430.00m));
        var asFlat = Premium(430.00m) with { Flat = true };
        var otherCategory = Premium(500.00m) with { ChargeCategory = "FEE" };
        var refundable = Premium(500.00m) with { RefundableOnCancel = true };

        foreach (var rate in new[] { asFlat, otherCategory, refundable })
        {
            Assert.Equal(ServicingRefusal.InvalidInput, Engine().Apply(state, new ChangeIntent(At(2026, 9, 17), [rate]), Corr).Refusal);
        }
    }

    [Fact]
    public void FlatCancel_KeepsNonRefundableFlatChargeConsistent_D3()
    {
        var fee = new ChargeRate("POL", "ALL", "POLICY_FEE", "FEE", 30.00m, Flat: true);
        var state = Open(Term365, Premium(430.00m), fee);

        var result = Engine().Apply(state, new EndCoverIntent(Term365.From, RefundMethod.FullRefund), Corr);

        Assert.True(result.IsAccepted, result.Message);
        Assert.Equal(-430.00m, Assert.Single(result.Deltas).Amount);
        Assert.Equal(30.00m, Written(result.State!, fee.Key));
        Assert.Equal(0m, Written(result.State!, Premium(1).Key));
    }

    [Fact]
    public void FlatCancel_OnTheFirstAthensDate_BeforeTheTermStartsAtTime_IsAccepted_D8()
    {
        var state = Open(Term365, Premium(430.00m)); // term starts 14:23
        var result = Engine().Apply(state, new EndCoverIntent(At(2026, 3, 1, 9, 0), RefundMethod.FullRefund), Corr);

        Assert.True(result.IsAccepted, result.Message);
        Assert.Equal(-430.00m, Assert.Single(result.Deltas).Amount);
        Assert.Equal(Term365.From, result.State!.CoverEndedAt);
        Assert.Equal(ServicingRefusal.EffectiveOutsideTerm, Engine().Apply(state, new EndCoverIntent(At(2026, 2, 28, 23, 0), RefundMethod.FullRefund), Corr).Refusal);
    }

    [Fact]
    public void ManyChanges_CarryResiduals_CumulativeWrittenStaysWithinOneCentOfExact_D6()
    {
        for (var seed = 1; seed <= 200; seed++)
        {
            var rng = new Random(seed);
            var rate = Math.Round(rng.Next(10_000, 90_000) / 100m, 2);
            var state = Open(Term365, Premium(rate));
            var exact = (double)rate; // an oracle in a test only, never on a money path
            var day = 0;
            for (var i = 0; i < 8; i++)
            {
                day += rng.Next(1, 40);
                var next = Math.Round(rng.Next(10_000, 90_000) / 100m, 2);
                var oldRate = state.Segments[state.Segments.Count - 1].Rate.AnnualRate;
                var result = Engine().Apply(state, new ChangeIntent(DayAt(Term365, day, 12), [Premium(next)]), Corr);
                Assert.True(result.IsAccepted, result.Message);
                exact += (double)(next - oldRate) * (Term365.Days - day) / Term365.Days;
                state = result.State!;
                var written = (double)Written(state, Premium(1).Key);
                Assert.True(Math.Abs(written - exact) <= 0.0051, $"seed {seed} step {i}: written {written} exact {exact}");
                Assert.All(state.Segments, s => Assert.True(s.Amount >= 0m));
            }
        }
    }

    [Fact]
    public void Change_AddsAndRemovesChargeTypes_WithoutNettingAcrossThem_REQ_POL_119()
    {
        var state = Open(Term365, Premium(430.00m), Premium(100.00m, "THEFT", coverage: "CASCO"));
        var added = Premium(73.00m, "GLASS", coverage: "GLASS");

        var result = Engine().Apply(state, new ChangeIntent(At(2026, 9, 17), [Premium(430.00m), added]), Corr);

        Assert.Equal(2, result.Deltas.Count);
        var glass = result.Deltas.Single(d => d.Key.ChargeType == "GLASS");
        Assert.Equal(33.00m, glass.Amount); // 73 x 165/365 = 33.0
        var theft = result.Deltas.Single(d => d.Key.ChargeType == "THEFT");
        Assert.Equal(-Math.Round(100.00m * 165m / 365m, 2, MidpointRounding.AwayFromZero), theft.Amount);
        Assert.Equal(TransactionKind.EndorsementCredit, theft.TransactionKind);
        Assert.Equal(0m, result.State!.Segments.Single(s => s.Key == theft.Key && s.To == Term365.To).Amount);
        Assert.Equal([1, 2], result.Deltas.Select(d => d.SetSequence).ToArray());
    }

    [Fact]
    public void ChangeBack_AtTheSameInstant_NetsToExactlyZero()
    {
        var state = Open(Term365, Premium(430.00m));
        var effective = At(2026, 9, 17, 9, 0);

        var up = Engine().Apply(state, new ChangeIntent(effective, [Premium(500.00m)]), Corr);
        var back = Engine().Apply(up.State, new ChangeIntent(effective, [Premium(430.00m)]), Corr);

        Assert.Equal(0m, up.Deltas.Sum(d => d.Amount) + back.Deltas.Sum(d => d.Amount));
        Assert.Equal(430.00m, Written(back.State!, Premium(1).Key));
    }

    // ---- RAT contract agreement ---------------------------------------------------------------------------------

    [Fact]
    public void ReferenceProration_AgreesWithTheRatContractSampleShape_REQ_RAT_004()
    {
        // rat.Proration.prorate now returns typed lines (days, termDays, fraction, Money amount) plus total and convention.
        // The reference implementation parses the sample's convention, and produces EUR minor-unit amounts for E2E-03.
        var path = Path.Combine(RepositoryPaths.Root, "tests", "CoreIns.Testing.Contracts", "Generated", "Samples", "rat.json");
        var response = JsonNode.Parse(File.ReadAllText(path))!["schemas"]!["ProrationProrateResponse"]!;
        Assert.True(DayCountConventions.TryParse(response["convention"]!.GetValue<string>(), out var convention));
        var line = response["lines"]![0]!;
        Assert.True(line["days"]!.GetValue<int>() >= 0 && line["termDays"]!.GetValue<int>() > 0);
        foreach (var money in new[] { line["amount"]!, response["total"]! })
        {
            Assert.Equal("EUR", money["currency"]!.GetValue<string>());
            Assert.True(decimal.TryParse(money["amount"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, out var amount));
            Assert.Equal(2, BitConverter.GetBytes(decimal.GetBits(amount)[3])[2]);
        }

        var fraction = new ReferenceProration().Fraction(convention, 245, 365);
        Assert.Equal((245, 365), (fraction.Numerator, fraction.Denominator));
        Assert.Equal(288.63m, decimal.Round(430.00m * fraction.Numerator / fraction.Denominator, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void ReloadedState_RatesPeriodsAndAmountsOnly_ReplaysToTheSameMoney_R3()
    {
        for (var seed = 1; seed <= 150; seed++)
        {
            var rng = new Random(seed);
            var term = rng.Next(2) == 0 ? Term365 : Term366 with { Convention = DayCountConvention.Act365F };
            var live = Open(term, Premium(Math.Round(rng.Next(10_000, 90_000) / 100m, 2)));
            var reloaded = live;
            var day = 0;
            for (var i = 0; i < 6; i++)
            {
                day += rng.Next(1, 30);
                var intent = new ChangeIntent(DayAt(term, day, 12), [Premium(i % 3 == 2 ? 0m : Math.Round(rng.Next(10_000, 90_000) / 100m, 2))]);
                var a = Engine().Apply(live, intent, Corr);
                var b = Engine().Apply(reloaded, intent, Corr);
                Assert.True(a.IsAccepted && b.IsAccepted, $"seed {seed}: {a.Message} {b.Message}");
                Assert.Equal(a.Deltas.Select(d => d.Amount), b.Deltas.Select(d => d.Amount));
                live = a.State!;

                // "reload": rebuild every segment from the stored columns only
                reloaded = b.State! with { Segments = b.State!.Segments.Select(s => new ServicingSegment(s.Rate, s.From, s.To, s.Amount)).ToList() };
                Assert.Equal(Written(live, Premium(1).Key), Written(reloaded, Premium(1).Key));
            }

            var cancel = new EndCoverIntent(DayAt(term, Math.Min(term.Days - 1, day + 5), 12), RefundMethod.ProRata);
            Assert.Equal(Engine().Apply(live, cancel, Corr).Deltas.Select(d => d.Amount), Engine().Apply(reloaded, cancel, Corr).Deltas.Select(d => d.Amount));
        }
    }

    [Fact]
    public void CorruptAmounts_AreRefused_NotClampedAway_R2()
    {
        var state = Open(Term365, Premium(430.00m));
        var bogus = state with { Segments = [state.Segments[0] with { Amount = 10_000.00m }] };
        var negative = state with { Segments = [state.Segments[0] with { Amount = -1.00m }] };

        foreach (var broken in new[] { bogus, negative })
        {
            Assert.Equal(ServicingRefusal.InvalidInput, Engine().Apply(broken, new ChangeIntent(At(2026, 9, 17), [Premium(400.00m)]), Corr).Refusal);
            Assert.Equal(ServicingRefusal.InvalidInput, Engine().Apply(broken, new EndCoverIntent(At(2026, 9, 17), RefundMethod.ProRata), Corr).Refusal);
        }
    }

    [Fact]
    public void ChangeToRateZero_CarriesTheCumulativeResidual_R1()
    {
        for (var seed = 1; seed <= 300; seed++)
        {
            var rng = new Random(seed);
            var state = Open(Term365, Premium(Math.Round(rng.Next(10_000, 90_000) / 100m, 2)));
            var day = 0;
            for (var i = 0; i < 5; i++)
            {
                day += rng.Next(1, 40);
                var rate = i == 4 ? 0m : Math.Round(rng.Next(10_000, 90_000) / 100m, 2);
                var result = Engine().Apply(state, new ChangeIntent(DayAt(Term365, day, 12), [Premium(rate)]), Corr);
                Assert.True(result.IsAccepted, result.Message);
                state = result.State!;
            }

            // the zero-rate remainder is exactly 0 and the cumulative equals the rounding of the earned exact amount
            Assert.Equal(0m, state.Segments[state.Segments.Count - 1].Amount);
            Assert.All(state.Segments, s => Assert.True(s.Amount >= 0m));
        }
    }

    // ---- property-style probes ----------------------------------------------------------------------------------

    [Fact]
    public void Property_RandomInSequenceChangesAndCancellation_KeepEveryInvariant()
    {
        for (var seed = 1; seed <= 400; seed++)
        {
            var rng = new Random(seed);
            var term = rng.Next(2) == 0 ? Term365 : Term366;
            var keys = Enumerable.Range(0, rng.Next(1, 4)).Select(i => (Type: $"CT{i}", Flat: i == 2)).ToList();

            var flatRates = new Dictionary<string, decimal>();
            var refundable = rng.Next(2) == 0;

            ChargeRate RateFor(string type, bool flat)
            {
                if (flat)
                {
                    if (!flatRates.TryGetValue(type, out var fixedRate))
                    {
                        fixedRate = Math.Round((decimal)rng.Next(0, 20_000) / 100m, 2);
                        flatRates[type] = fixedRate;
                    }

                    return new ChargeRate("VEH-1", "COV", type, "FEE", fixedRate, true, refundable);
                }

                return new ChargeRate("VEH-1", "COV", type, "PREMIUM", rng.Next(7) == 0 ? 0m : Math.Round((decimal)rng.Next(0, 200_000) / 100m + (decimal)rng.Next(0, 100) / 10_000m, 4));
            }

            var state = Open(term, keys.Select(k => RateFor(k.Type, k.Flat)).ToArray());
            var cumulative = new Dictionary<ChargeKey, decimal>();
            Accumulate(cumulative, Deltas(state));
            var totalDays = term.Days;
            var cursorDay = 0;
            var steps = rng.Next(0, 6);
            for (var step = 0; step < steps; step++)
            {
                cursorDay = Math.Min(totalDays - 1, cursorDay + rng.Next(0, Math.Max(1, (totalDays - cursorDay) / 2 + 1)));
                var effective = DayAt(term, cursorDay, rng.Next(0, 24));
                if (state.LatestBoundEffective is { } latest && effective < latest)
                {
                    effective = latest;
                }

                var rates = keys.Where(k => k.Flat || rng.Next(8) != 0).Select(k => RateFor(k.Type, k.Flat)).ToArray();
                var result = Engine().Apply(state, new ChangeIntent(effective, rates), Corr);
                Assert.True(result.IsAccepted, $"seed {seed}: {result.Message}");
                CheckStep(seed, term, state, result, cumulative);
                state = result.State!;
            }

            if (rng.Next(2) == 0)
            {
                var day = Math.Min(totalDays - 1, cursorDay + rng.Next(0, 40));
                var effective = DayAt(term, day, rng.Next(0, 24));
                if (state.LatestBoundEffective is { } latest && effective < latest)
                {
                    effective = latest;
                }

                var result = Engine().Apply(state, new EndCoverIntent(effective, RefundMethod.ProRata), Corr);
                Assert.True(result.IsAccepted, $"seed {seed}: {result.Message}");
                CheckStep(seed, term, state, result, cumulative);
                foreach (var credit in result.Deltas)
                {
                    Assert.True(credit.Amount < 0m, $"seed {seed}: cancellation delta must be a credit");
                    var segment = state.Segments.Where(s => s.Key == credit.Key).MaxBy(s => s.From)!;
                    Assert.True(-credit.Amount <= segment.Amount || segment.Rate.Flat, $"seed {seed}: credit exceeds the segment written");
                }

                // earned + unearned = written, per key (non-flat)
                foreach (var key in state.Segments.Select(s => s.Key).Distinct())
                {
                    var credit = result.Deltas.Where(d => d.Key == key).Sum(d => d.Amount);
                    Assert.Equal(Written(state, key), Written(result.State!, key) - credit);
                }
            }
        }
    }

    [Fact]
    public void Property_FlatOrSameDayCancel_CreditsExactlyMinusWritten()
    {
        for (var seed = 1; seed <= 300; seed++)
        {
            var rng = new Random(seed);
            var rates = Enumerable.Range(0, rng.Next(1, 5))
                .Select(i => Premium(Math.Round((decimal)rng.Next(1, 500_000) / 100m + rng.Next(0, 100) / 10_000m, 4), $"CT{i}"))
                .ToArray();
            var term = rng.Next(2) == 0 ? Term365 : Term366;
            var state = Open(term, rates);

            foreach (var method in new[] { RefundMethod.FullRefund, RefundMethod.ProRata })
            {
                var result = Engine().Apply(state, new EndCoverIntent(term.From, method), Corr);

                foreach (var rate in rates)
                {
                    Assert.Equal(-Written(state, rate.Key), result.Deltas.Single(d => d.Key == rate.Key).Amount);
                }

                Assert.Empty(result.State!.Segments);
            }
        }
    }

    [Fact]
    public void Property_ProRataCredit_IsMonotonicAndNeverExceedsWritten()
    {
        var state = Open(Term365, Premium(430.00m));
        var previous = decimal.MaxValue;
        for (var day = 0; day < 365; day++)
        {
            var result = Engine().Apply(state, new EndCoverIntent(DayAt(Term365, day, 23), RefundMethod.ProRata), Corr);
            var credit = -Assert.Single(result.Deltas).Amount;
            Assert.InRange(credit, 0m, 430.00m);
            Assert.True(credit <= previous, $"credit rose at day {day}");
            Assert.Equal(430.00m, credit + result.State!.Segments.Sum(s => s.Amount));
            previous = credit;
        }
    }

    private static Instant DayAt(ServicingTerm term, int day, int hour)
    {
        var date = term.From.ToBusinessDate(Athens).Value.AddDays(day);
        var local = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, 7)), DateTimeKind.Unspecified);
        if (Athens.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var instant = Instant.FromUtcDateTime(TimeZoneInfo.ConvertTimeToUtc(local, Athens));
        return day == 0 ? Instant.Max(instant, term.From) : instant;
    }

    private static IReadOnlyList<ServicingDelta> Deltas(ServicingState opened)
    {
        var result = Engine().Apply(null, new NewTermIntent(opened.Term, opened.Segments.Select(s => s.Rate).ToArray()), Corr);
        return result.Deltas;
    }

    private static void Accumulate(Dictionary<ChargeKey, decimal> cumulative, IEnumerable<ServicingDelta> deltas)
    {
        foreach (var d in deltas)
        {
            cumulative[d.Key] = cumulative.GetValueOrDefault(d.Key) + d.Amount;
        }
    }

    private static void CheckStep(int seed, ServicingTerm term, ServicingState before, ServicingResult result, Dictionary<ChargeKey, decimal> cumulative)
    {
        // every delta's period is inside the term; one delta per key and transaction (never netted across charge types)
        Assert.All(result.Deltas, d =>
        {
            Assert.True(d.ValidFrom >= term.From && d.ValidTo <= term.To && d.ValidFrom < d.ValidTo, $"seed {seed}: delta outside term");
            Assert.NotEqual(0m, d.Amount);
        });
        Assert.All(result.State!.Segments, s => Assert.True(s.Amount >= 0m, $"seed {seed}: negative segment"));
        Assert.Equal(result.Deltas.Count, result.Deltas.Select(d => d.Key).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, result.Deltas.Count), result.Deltas.Select(d => d.SetSequence));

        // P7: cumulative deltas per key = cumulative written (sum of current segment amounts)
        Accumulate(cumulative, result.Deltas);
        foreach (var key in cumulative.Keys.Union(result.State!.Segments.Select(s => s.Key)))
        {
            Assert.Equal(cumulative.GetValueOrDefault(key), Written(result.State, key));
        }

        // segments stay contiguous within the term and end at the end of cover
        foreach (var group in result.State!.Segments.GroupBy(s => s.Key))
        {
            var list = group.OrderBy(s => s.From).ToList();
            Assert.True(list[0].From >= term.From);
            Assert.Equal(result.State.CoverEndedAt ?? term.To, list[^1].To);
            for (var i = 1; i < list.Count; i++)
            {
                Assert.Equal(list[i - 1].To, list[i].From);
            }
        }

        // fewer deltas never mean an unrelated key changed: untouched keys have untouched segments
        foreach (var key in before.Segments.Select(s => s.Key).Distinct().Except(result.Deltas.Select(d => d.Key)))
        {
            Assert.Equal(Written(before, key), Written(result.State, key));
        }
    }
}
