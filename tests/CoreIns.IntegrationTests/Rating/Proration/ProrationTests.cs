using System.Text.Json;
using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Handling = CoreIns.Modules.Rating.Contracts.Api.ProrationAnnualRate.HandlingValue;

namespace CoreIns.IntegrationTests.Rating.Proration;

/// <summary>
/// SL3-RAT-PRORATE: <c>rat.Proration.prorate</c> over the real MKT rounding rule of the Greece pack and a scripted product artefact that
/// declares the day count (no database). Requirement ids in test names: REQ-RAT-004 amounts, -155/-156 conventions, -157 half-open Athens
/// dates, -158 term-length sum, -160 flat charges, -163 exact reversal, -165 undeclared convention, -276 six-month golden.
/// Every number is synthetic test data.
/// </summary>
public sealed class ProrationTests
{
    private static readonly Currency Eur = Currency.EUR;
    private static readonly Sha256Hash ProductHash = Sha256Hash.Parse(new string('b', 64));
    private static readonly ConfigurationHash Config = ConfigurationHash.Parse(new string('a', 64));

    // A 365-day term, 2026-03-01 .. 2027-03-01.
    private static readonly BusinessDate T0 = new(2026, 3, 1);
    private static readonly BusinessDate T1 = new(2027, 3, 1);

    [Fact]
    public async Task REQ_RAT_004_TERM_RATIO_100_day_segment_of_a_365_rate_is_100_00_and_the_reversed_period_is_minus_100_00()
    {
        var forward = await Run(DayCount.TermRatio, T0, T1, [Per("S1", T0, T0.AddDays(100))], [Rate("S1", 365.0000m)]);
        var reversed = await Run(DayCount.TermRatio, T0, T1, [Per("S1", T0, T0.AddDays(100), reverse: true)], [Rate("S1", 365.0000m)]);

        var line = forward.Lines.Single();
        line.Days.ShouldBe(100);
        line.TermDays.ShouldBe(365);
        line.Amount.Amount.ShouldBe(100.00m);
        line.Residual.Amount.ShouldBe(0m);
        line.Fraction.ShouldBe(100m / 365m, 1e-15m);
        forward.Convention.ShouldBe(DayCountConvention.TermRatio);
        forward.ConfigurationHash.ShouldBe(Config);
        reversed.Lines.Single().Amount.Amount.ShouldBe(-100.00m);
        reversed.Lines.Single().Residual.Amount.ShouldBe(0m);
        reversed.Total.Amount.ShouldBe(-100.00m);
    }

    [Fact]
    public async Task REQ_RAT_004_a_term_split_into_120_and_245_days_adds_back_to_the_annual_rate()
    {
        var split = T0.AddDays(120);
        var result = await Run(DayCount.TermRatio, T0, T1, [Per("S1", T0, split), Per("S2", split, T1)], [Rate("S1", 365.0000m), Rate("S2", 365.0000m)]);

        result.Lines.Select(l => l.Days).ShouldBe([120, 245]);
        result.Lines.Select(l => l.Amount.Amount).ShouldBe([120.00m, 245.00m]);
        result.Total.Amount.ShouldBe(365.00m);
    }

    [Fact]
    public async Task D_SL3_04_the_E2E_03_refund_is_288_63_on_a_430_00_premium_at_day_120_and_earned_plus_unearned_is_the_written_amount()
    {
        var split = T0.AddDays(120);
        var result = await Run(DayCount.TermRatio, T0, T1, [Per("elapsed", T0, split), Per("unearned", split, T1)], [Rate("elapsed", 430.00m), Rate("unearned", 430.00m)]);

        result.Lines[0].Amount.Amount.ShouldBe(141.37m);
        result.Lines[1].Amount.Amount.ShouldBe(288.63m);
        result.Total.Amount.ShouldBe(430.00m);
        foreach (var line in result.Lines)
        {
            (line.Amount.Amount + line.Residual.Amount).ShouldBe(UnroundedOf(line)); // residual makes it exact
        }
    }

    [Fact]
    public async Task REQ_RAT_276_a_182_day_six_month_term_of_annual_rate_365_is_182_50_in_full_and_the_reversal_is_minus_182_50()
    {
        var start = new BusinessDate(2024, 1, 1);
        var end = new BusinessDate(2024, 7, 1); // 182 days, six calendar months
        var full = await Run(DayCount.TermRatio, start, end, [Per("S", start, end)], [Rate("S", 365.0000m)]);
        var back = await Run(DayCount.TermRatio, start, end, [Per("S", start, end, reverse: true)], [Rate("S", 365.0000m)]);

        full.Lines.Single().Days.ShouldBe(182);
        full.Lines.Single().TermDays.ShouldBe(182);
        full.Lines.Single().Amount.Amount.ShouldBe(182.50m);
        back.Lines.Single().Amount.Amount.ShouldBe(-182.50m);

        // Two halves of that term add up to the same 182.50 (91 + 91 days).
        var mid = start.AddDays(91);
        var halves = await Run(DayCount.TermRatio, start, end, [Per("A", start, mid), Per("B", mid, end)], [Rate("A", 365.0000m), Rate("B", 365.0000m)]);
        halves.Total.Amount.ShouldBe(182.50m);
        halves.Lines.Select(l => l.Amount.Amount).ShouldBe([91.25m, 91.25m]);
    }

    [Fact]
    public async Task REQ_RAT_155_a_leap_year_full_term_is_the_rate_under_TERM_RATIO_and_366_over_365_of_it_under_ACT_365F()
    {
        var start = new BusinessDate(2023, 3, 1);
        var end = new BusinessDate(2024, 3, 1); // 366 days, contains 29 Feb 2024
        var termRatio = await Run(DayCount.TermRatio, start, end, [Per("S", start, end)], [Rate("S", 430.00m)]);
        var act365 = await Run(DayCount.Act365f, start, end, [Per("S", start, end)], [Rate("S", 430.00m)]);

        termRatio.Lines.Single().TermDays.ShouldBe(366);
        termRatio.Lines.Single().Amount.Amount.ShouldBe(430.00m);
        act365.Lines.Single().Days.ShouldBe(366);
        UnroundedOf(act365.Lines.Single()).ShouldBe(431.1780821917808219m); // 430 x 366 / 365 truncated at 16 places
        act365.Lines.Single().Amount.Amount.ShouldBe(431.18m);
    }

    [Fact]
    public async Task REQ_RAT_156_ACT_365F_half_year_of_a_366_rate_is_182_50()
    {
        var start = new BusinessDate(2024, 1, 1);
        var term = new BusinessDate(2025, 1, 1);
        var result = await Run(DayCount.Act365f, start, term, [Per("H1", start, new BusinessDate(2024, 7, 1))], [Rate("H1", 366.00m)]);

        result.Lines.Single().Amount.Amount.ShouldBe(182.50m); // 366.00 x 182 / 365 = 182.4986...
    }

    [Theory]
    [InlineData("ACT/365F", "TERM_RATIO")] // the artefact declares ACT/365F, the caller asks for TERM_RATIO
    [InlineData("TERM_RATIO", "ACT_365F")] // and the other way round
    [InlineData("ACT/ACT", "ACT_ACT")] // declared and known to the PRD, but not built
    [InlineData("30E/360", "THIRTY_E_360")]
    [InlineData(null, "TERM_RATIO")] // the artefact declares nothing
    [InlineData("MADE_UP", "TERM_RATIO")]
    public async Task REQ_RAT_165_a_caller_cannot_pick_a_convention_the_artefact_does_not_declare(string? declared, string requested)
    {
        var convention = requested switch
        {
            "TERM_RATIO" => DayCountConvention.TermRatio,
            "ACT_365F" => DayCountConvention.Act365f,
            "ACT_ACT" => DayCountConvention.ActAct,
            _ => DayCountConvention.ThirtyE360,
        };

        var ex = await Should.ThrowAsync<DomainException>(() => Engine(declared).ProrateAsync(
            Request(convention, T0, T1, [Per("S", T0, T1)], [Rate("S", 365m)]), TestContext.Current.CancellationToken));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-CONVENTION");
    }

    [Fact]
    public async Task An_unknown_product_artefact_is_RAT_ERR_INPUT()
    {
        var ex = await Should.ThrowAsync<DomainException>(() => Engine("TERM_RATIO", unknown: true).ProrateAsync(
            Request(DayCountConvention.TermRatio, T0, T1, [Per("S", T0, T1)], [Rate("S", 365m)]), TestContext.Current.CancellationToken));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
    }

    [Fact]
    public async Task A_provisional_day_count_is_reported_in_the_explanation()
    {
        var engine = Engine("TERM_RATIO", provisional: true);
        var result = await engine.ProrateAsync(Request(DayCountConvention.TermRatio, T0, T1, [Per("S", T0, T1)], [Rate("S", 365m)]), TestContext.Current.CancellationToken);

        result.Explanation!.Value.GetProperty("conventionProvisional").GetBoolean().ShouldBeTrue();
        result.Explanation!.Value.GetProperty("conventionNote").GetString()!.ShouldContain("finance");
    }

    [Fact]
    public async Task REQ_RAT_157_29_February_is_a_normal_calendar_day_whichever_convention_is_used()
    {
        // A term bound on 29 Feb 2024 ends on 28 Feb 2025 (AddYears): 365 days, 12 months.
        var start = new BusinessDate(2024, 2, 29);
        var end = start.AddYears(1);
        end.ShouldBe(new BusinessDate(2025, 2, 28));
        var oneDay = await Run(DayCount.TermRatio, start, end, [Per("S", start, start.AddDays(1))], [Rate("S", 365.00m)]);
        oneDay.Lines.Single().TermDays.ShouldBe(365);
        oneDay.Lines.Single().Days.ShouldBe(1);
        oneDay.Lines.Single().Amount.Amount.ShouldBe(1.00m);

        // A 366-day term containing 29 Feb: the 2 days 28 Feb and 29 Feb.
        var t0 = new BusinessDate(2023, 3, 1);
        var t1 = new BusinessDate(2024, 3, 1);
        var feb28 = new BusinessDate(2024, 2, 28);
        (await Run(DayCount.TermRatio, t0, t1, [Per("S", feb28, t1)], [Rate("S", 366.00m)])).Lines.Single().Amount.Amount.ShouldBe(2.00m);
        (await Run(DayCount.Act365f, t0, t1, [Per("S", feb28, t1)], [Rate("S", 365.00m)])).Lines.Single().Amount.Amount.ShouldBe(2.00m);
        (await Run(DayCount.TermRatio, t0, t1, [Per("S", new BusinessDate(2024, 2, 29), t1)], [Rate("S", 366.00m)])).Lines.Single().Days.ShouldBe(1);
    }

    [Fact]
    public async Task REQ_RAT_157_days_are_whole_calendar_dates_so_the_daylight_saving_change_adds_no_hour()
    {
        // Athens clocks go forward on 2026-03-29; whole dates make [28 Mar, 30 Mar) exactly 2 days.
        var result = await Run(DayCount.TermRatio, T0, T1, [Per("S", new BusinessDate(2026, 3, 28), new BusinessDate(2026, 3, 30))], [Rate("S", 365.00m)]);

        result.Lines.Single().Days.ShouldBe(2);
        result.Lines.Single().Amount.Amount.ShouldBe(2.00m);
    }

    [Fact]
    public void A_zero_day_segment_is_worth_nothing_and_a_zero_day_term_is_refused()
    {
        var line = new CalcLine("S", "V1", "MTPL", "PREM-MTPL", "PREMIUM", 430.00m, Handling.Proratable);
        var zero = ProrationCalculator.Prepare(new CalcInput(Eur, T0, T1, DayCountConvention.TermRatio, [new CalcSegment("S", T0, T0, false)], [line]));
        zero.Lines.Single().Days.ShouldBe(0);
        zero.Lines.Single().Unrounded.ShouldBe(0m);

        var ex = Should.Throw<DomainException>(() => ProrationCalculator.Prepare(
            new CalcInput(Eur, T0, T0, DayCountConvention.TermRatio, [new CalcSegment("S", T0, T0, false)], [line])));
        ex.Error.Code.Value.ShouldBe("RAT-ERR-PERIOD");
    }

    [Fact]
    public async Task A_term_that_is_not_whole_months_is_refused_under_TERM_RATIO_and_an_open_ended_term_always()
    {
        var odd = await Should.ThrowAsync<DomainException>(() => Run(DayCount.TermRatio, T0, T0.AddDays(100), [Per("S", T0, T0.AddDays(10))], [Rate("S", 365m)]));
        odd.Error.Code.Value.ShouldBe("RAT-ERR-PERIOD");
        (await Run(DayCount.Act365f, T0, T0.AddDays(100), [Per("S", T0, T0.AddDays(10))], [Rate("S", 365m)])).Lines.Single().Amount.Amount.ShouldBe(10.00m);

        var request = Request(DayCountConvention.TermRatio, T0, T1, [Per("S", T0, T1)], [Rate("S", 365m)]) with { Term = DateRange.Open(T0) };
        var open = await Should.ThrowAsync<DomainException>(() => Engine("TERM_RATIO").ProrateAsync(request, TestContext.Current.CancellationToken));
        open.Error.Code.Value.ShouldBe("RAT-ERR-PERIOD");
    }

    [Fact]
    public async Task A_full_term_segment_is_exactly_the_annual_rate_and_the_rounding_residual_is_returned()
    {
        var result = await Run(DayCount.TermRatio, T0, T1, [Per("S", T0, T1)], [Rate("S", 123.4567m)]);

        var line = result.Lines.Single();
        UnroundedOf(line).ShouldBe(123.4567m);
        line.Amount.Amount.ShouldBe(123.46m);
        line.Residual.Amount.ShouldBe(-0.0033m);
        line.Fraction.ShouldBe(1m);
    }

    [Theory]
    [InlineData(-5, 10)] // starts before the term
    [InlineData(10, 400)] // ends after the term
    public async Task REQ_RAT_157_a_segment_outside_the_term_is_RAT_ERR_PERIOD(int fromDays, int toDays)
    {
        var ex = await Should.ThrowAsync<DomainException>(() => Run(DayCount.TermRatio, T0, T1, [Per("S", T0.AddDays(fromDays), T0.AddDays(toDays))], [Rate("S", 365m)]));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-PERIOD");
    }

    [Fact]
    public async Task Overlapping_segments_of_one_line_are_RAT_ERR_PERIOD_but_the_same_dates_for_different_lines_are_fine()
    {
        var ex = await Should.ThrowAsync<DomainException>(() => Run(DayCount.TermRatio, T0, T1,
            [Per("A", T0, T0.AddDays(200)), Per("B", T0.AddDays(100), T1)], [Rate("A", 365m), Rate("B", 365m)]));
        ex.Error.Code.Value.ShouldBe("RAT-ERR-PERIOD");

        var other = Rate("B", 365m) with { ChargeType = "PREM-OD" };
        var fine = await Run(DayCount.TermRatio, T0, T1, [Per("A", T0, T0.AddDays(200)), Per("B", T0.AddDays(100), T1)], [Rate("A", 365m), other]);
        fine.Lines.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Duplicate_segment_ids_and_rates_for_unknown_segments_are_RAT_ERR_INPUT()
    {
        (await Should.ThrowAsync<DomainException>(() => Run(DayCount.TermRatio, T0, T1, [Per("A", T0, T0.AddDays(10)), Per("A", T0.AddDays(10), T1)], [Rate("A", 365m)])))
            .Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
        (await Should.ThrowAsync<DomainException>(() => Run(DayCount.TermRatio, T0, T1, [Per("A", T0, T1)], [Rate("Z", 365m)])))
            .Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
    }

    [Fact]
    public async Task REQ_RAT_160_a_flat_fee_is_charged_at_inception_and_a_mid_term_change_segment_carries_none()
    {
        var change = T0.AddDays(120);
        var rates = new[]
        {
            Rate("inception", 25.00m, Handling.Flat, "FEE"), Rate("change", 25.00m, Handling.Flat, "FEE"),
            Rate("inception", 7.50m, Handling.FullyEarned, "EARNED"), Rate("inception", 365m),
        };
        var result = await Run(DayCount.TermRatio, T0, T1, [Per("inception", T0, change), Per("change", change, T1)], rates);

        result.Lines.Single(l => l.SegmentId == "inception" && l.ChargeType == "FEE").Amount.Amount.ShouldBe(25.00m);
        result.Lines.Single(l => l.SegmentId == "change" && l.ChargeType == "FEE").Amount.Amount.ShouldBe(0m); // no fee for the change
        result.Lines.Single(l => l.ChargeType == "EARNED").Amount.Amount.ShouldBe(7.50m);
        result.Lines.Single(l => l.ChargeType == "PREM-MTPL").Amount.Amount.ShouldBe(120.00m);
    }

    [Fact]
    public async Task Minimum_premium_is_not_applied_here_a_tiny_prorated_amount_rounds_to_zero_and_keeps_its_residual()
    {
        // D-SL3-04: minimum premium and short-rate are not in the slice. 0.10 x 1 / 365 = 0.000273972... -> 0.00.
        var result = await Run(DayCount.TermRatio, T0, T1, [Per("S", T0, T0.AddDays(1))], [Rate("S", 0.10m)]);

        var line = result.Lines.Single();
        line.Amount.Amount.ShouldBe(0m);
        line.Residual.Amount.ShouldBeGreaterThan(0m);
        line.RoundingRuleId.ShouldNotBeNullOrEmpty(); // set even when nothing is left to round
    }

    [Fact]
    public async Task REQ_RAT_163_a_half_cent_rounds_away_from_zero_in_both_directions_so_the_credit_is_the_exact_negative_of_the_debit()
    {
        // 1.825 x 1 / 365 = 0.005 exactly: the HALF_UP tie.
        var debit = (await Run(DayCount.TermRatio, T0, T1, [Per("S", T0, T0.AddDays(1))], [Rate("S", 1.825m)])).Lines.Single();
        var credit = (await Run(DayCount.TermRatio, T0, T1, [Per("S", T0, T0.AddDays(1))], [Rate("S", -1.825m)])).Lines.Single();
        var reversed = (await Run(DayCount.TermRatio, T0, T1, [Per("S", T0, T0.AddDays(1), reverse: true)], [Rate("S", 1.825m)])).Lines.Single();

        debit.Amount.Amount.ShouldBe(0.01m);
        debit.Residual.Amount.ShouldBe(-0.005m);
        credit.Amount.Amount.ShouldBe(-0.01m);
        credit.Residual.Amount.ShouldBe(0.005m);
        reversed.Amount.ShouldBe(credit.Amount);
        reversed.Residual.ShouldBe(credit.Residual);
    }

    [Fact]
    public async Task REQ_RAT_158_REQ_RAT_163_contiguous_segments_add_up_to_the_term_amount_exactly_and_reverse_exactly()
    {
        var engine = Engine("TERM_RATIO");
        foreach (var rate in new[] { 430.00m, 123.4567m, 999.9999m, 0.0100m, 1000m })
        {
            for (var split = 1; split < 365; split += 7)
            {
                var mid = T0.AddDays(split);
                var periods = new[] { Per("A", T0, mid), Per("B", mid, T1) };
                var forward = await engine.ProrateAsync(Request(DayCountConvention.TermRatio, T0, T1, periods, [Rate("A", rate), Rate("B", rate)]), TestContext.Current.CancellationToken);
                var back = await engine.ProrateAsync(
                    Request(DayCountConvention.TermRatio, T0, T1, [Per("A", T0, mid, reverse: true), Per("B", mid, T1, reverse: true)], [Rate("A", rate), Rate("B", rate)]),
                    TestContext.Current.CancellationToken);

                // exact: the unrounded parts sum to the annual rate, no 1e-16 drift (REQ-RAT-158)
                forward.Lines.Sum(UnroundedOf).ShouldBe(rate);
                for (var i = 0; i < 2; i++)
                {
                    (forward.Lines[i].Amount + back.Lines[i].Amount).IsZero.ShouldBeTrue();
                    (forward.Lines[i].Residual + back.Lines[i].Residual).IsZero.ShouldBeTrue();
                    (forward.Lines[i].Amount.Amount + forward.Lines[i].Residual.Amount).ShouldBe(UnroundedOf(forward.Lines[i]));
                }
            }
        }
    }

    [Fact]
    public async Task REQ_RAT_158_three_thirds_of_a_term_sum_exactly_to_the_rate()
    {
        var a = T0.AddDays(122);
        var b = T0.AddDays(243);
        var result = await Run(DayCount.TermRatio, T0, T1, [Per("A", T0, a), Per("B", a, b), Per("C", b, T1)], [Rate("A", 100m), Rate("B", 100m), Rate("C", 100m)]);

        result.Lines.Sum(UnroundedOf).ShouldBe(100m);
    }

    [Fact]
    public async Task Segments_at_different_rates_are_not_forced_to_sum_to_one_rate()
    {
        var mid = T0.AddDays(100);
        var result = await Run(DayCount.TermRatio, T0, T1, [Per("A", T0, mid), Per("B", mid, T1)], [Rate("A", 365m), Rate("B", 730m)]);

        result.Lines.Select(l => l.Amount.Amount).ShouldBe([100.00m, 530.00m]); // 730 x 265 / 365
    }

    [Fact]
    public async Task The_rounding_rule_is_reported_on_every_line()
    {
        var result = await Run(DayCount.TermRatio, T0, T1, [Per("S", T0, T0.AddDays(100))], [Rate("S", 365.5m)]);

        result.Lines.Single().RoundingRuleId.ShouldBe("cur.rounding.charge.line");
    }

    [Fact]
    public async Task Bad_input_is_refused_not_guessed()
    {
        (await Should.ThrowAsync<DomainException>(() => Run(DayCount.TermRatio, T0, T1, [Per("S", T0, T1)], []))).Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
        (await Should.ThrowAsync<DomainException>(() => Run(DayCount.TermRatio, T0, T1, [], [Rate("S", 1m)]))).Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
        // a rate with more digits than a decimal can multiply exactly is refused, never silently rounded
        var ex = await Should.ThrowAsync<DomainException>(() => Run(DayCount.TermRatio, T0, T1, [Per("S", T0, T0.AddDays(3))], [Rate("S", 7922816251426433759354395.0m)]));
        ex.Error.Code.Value.ShouldBe("RAT-ERR-SCALE");
    }

    // ---- fixtures -------------------------------------------------------------------------------------------------------------

    private enum DayCount
    {
        TermRatio,
        Act365f,
    }

    private static Task<ProrationProrateResponse> Run(
        DayCount convention, BusinessDate from, BusinessDate to, ProrationPeriod[] periods, ProrationAnnualRate[] rates) =>
        Engine(convention == DayCount.TermRatio ? "TERM_RATIO" : "ACT/365F").ProrateAsync(
            Request(convention == DayCount.TermRatio ? DayCountConvention.TermRatio : DayCountConvention.Act365f, from, to, periods, rates),
            TestContext.Current.CancellationToken);

    private static ProrationProrateRequest Request(
        DayCountConvention convention, BusinessDate from, BusinessDate to, ProrationPeriod[] periods, ProrationAnnualRate[] rates) => new()
        {
            AnnualRates = rates,
            Term = DateRange.Of(from, to),
            Periods = periods,
            Convention = convention,
            ProductArtefactHash = ProductHash,
            ConfigurationHash = Config,
        };

    private static ProrationPeriod Per(string id, BusinessDate from, BusinessDate to, bool reverse = false) =>
        new() { SegmentId = id, Period = DateRange.Of(from, to), Reverse = reverse };

    private static ProrationAnnualRate Rate(string segment, decimal annual, Handling handling = Handling.Proratable, string chargeType = "PREM-MTPL") => new()
    {
        SegmentId = segment,
        ElementLocator = "V1",
        CoverageCode = "MTPL",
        ChargeType = chargeType,
        ChargeCategory = "PREMIUM",
        AnnualAmount = new Money(annual, Eur),
        Handling = handling,
    };

    /// <summary>The unrounded amount: amount + residual (the contract returns the residual, not the unrounded value).</summary>
    private static decimal UnroundedOf(ProrationLine line) => line.Amount.Amount + line.Residual.Amount;

    internal static RatingProrationEngine Engine(string? dayCount, bool provisional = false, bool unknown = false)
    {
        var entity = new LegalEntityInfo(
            new LegalEntityId(Guid.Parse("0192f0c4-0000-7000-8000-000000000001")), LegalEntityCode.Parse("GR-TEST"), "GR", "gr", "EUR", "Europe/Athens", "ACTIVE", true);
        var clock = new ManualClock(Instant.FromUtc(2026, 10, 7));
        var catalogue = new ConfigurationCatalogue([new GrPackConfiguration()], clock.Now);
        var market = new ConfigurationEngine(catalogue, new LegalEntityRegistry([entity]), new Env("Development"), clock);
        return new RatingProrationEngine(
            new MarketRoundingService(market), new ScriptedProducts(dayCount, provisional, unknown), new RequestContext { LegalEntity = LegalEntityCode.Parse("GR-TEST") });
    }

    private sealed class ScriptedProducts(string? dayCount, bool provisional, bool unknown) : IProductArtifactService
    {
        public Task<ArtifactGetResponse> GetAsync(string id, string? parts = null, CancellationToken cancellationToken = default)
        {
            if (unknown)
            {
                throw new DomainException(CoreIns.SharedKernel.Results.DomainError.Of(ModuleCode.PFC, "UNKNOWN-HASH"));
            }

            var json = dayCount is null
                ? "{}"
                : JsonSerializer.Serialize(new { dayCount, dayCountProvisional = provisional ? (bool?)true : null, dayCountNote = provisional ? "confirm with finance" : null });
            return Task.FromResult(new ArtifactGetResponse { CanonicalJsonArtefact = JsonDocument.Parse(json).RootElement.Clone() });
        }
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
