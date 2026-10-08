using CoreIns.CountryPacks.GR.Configuration;
using CoreIns.Modules.Market.Domain;
using CoreIns.Modules.Market.Queries;
using CoreIns.Modules.Market.Services;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreIns.IntegrationTests.Rating.Proration;

/// <summary>
/// SL3-RAT-PRORATE: <c>rat.Proration.prorate</c> over the real MKT rounding rule of the Greece pack (no database). Requirement ids in
/// test names: REQ-RAT-004 amounts, -155/-156 conventions, -157 half-open Athens dates, -158 term-length sum, -160 flat charges,
/// -163 exact reversal, -165 undeclared convention, -276 six-month golden. Every number is synthetic test data.
/// </summary>
public sealed class ProrationTests
{
    private static readonly Currency Eur = Currency.EUR;
    private static readonly string[] TermRatioOnly = [DayCountConventions.TermRatio];
    private static readonly string[] Both = [DayCountConventions.TermRatio, DayCountConventions.Act365Fixed];

    // A 365-day term, 2026-03-01 .. 2027-03-01.
    private static readonly BusinessDate T0 = new(2026, 3, 1);
    private static readonly BusinessDate T1 = new(2027, 3, 1);

    [Fact]
    public async Task REQ_RAT_004_TERM_RATIO_100_day_segment_of_a_365_rate_is_100_00_and_the_reversal_is_minus_100_00()
    {
        var result = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S1", T0, T0.AddDays(100), Line("L1", 365.0000m))));

        var line = result.Lines.Single();
        line.Days.ShouldBe(100);
        line.DaysDenominator.ShouldBe(365);
        line.Amount.Amount.ShouldBe(100.00m);
        line.Residual.Amount.ShouldBe(0m);
        line.Fraction.ShouldBe(100m / 365m, 1e-15m);
        result.TermDays.ShouldBe(365);
        result.Convention.ShouldBe("TERM_RATIO");

        var reversed = result.Reverse().Lines.Single();
        reversed.Amount.Amount.ShouldBe(-100.00m);
        reversed.Residual.Amount.ShouldBe(0m);
        (line.Amount + reversed.Amount).IsZero.ShouldBeTrue();
    }

    [Fact]
    public async Task REQ_RAT_004_a_term_split_into_120_and_245_days_adds_back_to_the_annual_rate()
    {
        var split = T0.AddDays(120);
        var result = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly,
            Seg("S1", T0, split, Line("L1", 365.0000m)), Seg("S2", split, T1, Line("L1", 365.0000m))));

        result.Lines.Select(l => l.Days).ShouldBe([120, 245]);
        result.Lines.Select(l => l.Amount.Amount).ShouldBe([120.00m, 245.00m]);
        result.Lines.Sum(l => l.Amount.Amount).ShouldBe(365.00m);
    }

    [Fact]
    public async Task D_SL3_04_the_E2E_03_refund_is_288_63_on_a_430_00_premium_at_day_120_and_earned_plus_unearned_is_the_written_amount()
    {
        var split = T0.AddDays(120);
        var result = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly,
            Seg("elapsed", T0, split, Line("L1", 430.00m)), Seg("unearned", split, T1, Line("L1", 430.00m))));

        var elapsed = result.Lines[0];
        var unearned = result.Lines[1];
        elapsed.Amount.Amount.ShouldBe(141.37m);
        unearned.Amount.Amount.ShouldBe(288.63m);
        (elapsed.Amount + unearned.Amount).Amount.ShouldBe(430.00m);
        // the residuals are returned so POL can keep cumulative amounts exact: unrounded = rounded + residual
        foreach (var line in result.Lines)
        {
            (line.Amount.Amount + line.Residual.Amount).ShouldBe(line.Unrounded);
        }
    }

    [Fact]
    public async Task REQ_RAT_155_a_leap_year_full_term_is_the_rate_under_TERM_RATIO_and_366_over_365_of_it_under_ACT_365F()
    {
        var start = new BusinessDate(2023, 3, 1);
        var end = new BusinessDate(2024, 3, 1); // 366 days, contains 29 Feb 2024
        var termRatio = await Engine().RunAsync(Request(start, end, "TERM_RATIO", Both, Seg("S", start, end, Line("L", 430.00m))));
        var act365 = await Engine().RunAsync(Request(start, end, "ACT/365F", Both, Seg("S", start, end, Line("L", 430.00m))));

        termRatio.TermDays.ShouldBe(366);
        termRatio.Lines.Single().Amount.Amount.ShouldBe(430.00m);
        termRatio.Lines.Single().Unrounded.ShouldBe(430m);
        act365.Lines.Single().DaysDenominator.ShouldBe(365);
        act365.Lines.Single().Days.ShouldBe(366);
        act365.Lines.Single().Unrounded.ShouldBe(431.1780821917808219m); // 430 x 366 / 365 truncated at 16 places
        act365.Lines.Single().Amount.Amount.ShouldBe(431.18m);
    }

    [Fact]
    public async Task REQ_RAT_276_the_182_day_half_year_golden_is_182_50()
    {
        var start = new BusinessDate(2024, 1, 1);
        var term = new BusinessDate(2025, 1, 1);
        var halfYear = new BusinessDate(2024, 7, 1); // 182 days in a leap year
        var act365 = await Engine().RunAsync(Request(start, term, "ACT/365F", Both, Seg("H1", start, halfYear, Line("L", 366.00m))));

        act365.Lines.Single().Days.ShouldBe(182);
        act365.Lines.Single().Amount.Amount.ShouldBe(182.50m); // 366.00 x 182 / 365 = 182.4986...
        act365.Lines.Single().Residual.Amount.ShouldBe(-0.0013698630136986m, 1e-16m);

        var termRatio = await Engine().RunAsync(Request(start, term, "TERM_RATIO", Both, Seg("H1", start, halfYear, Line("L", 366.00m))));
        termRatio.Lines.Single().Amount.Amount.ShouldBe(182.00m); // 366.00 x 182 / 366
    }

    [Theory]
    [InlineData("ACT/365F", "TERM_RATIO")] // declared TERM_RATIO only, requested ACT/365F
    [InlineData("TERM_RATIO", "")] // declares nothing
    [InlineData("ACT/ACT", "ACT/ACT")] // declared and known to the PRD, but not built
    [InlineData("30E/360", "30E/360")]
    [InlineData("", "TERM_RATIO")] // requests nothing
    [InlineData("MADE_UP", "MADE_UP")]
    public async Task REQ_RAT_165_a_convention_the_artefact_does_not_declare_is_refused(string requested, string declared)
    {
        var declaredList = declared.Length == 0 ? Array.Empty<string>() : new[] { declared };
        var ex = await Should.ThrowAsync<DomainException>(() => Engine().RunAsync(
            Request(T0, T1, requested, declaredList, Seg("S", T0, T1, Line("L", 365m)))));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-CONVENTION");
    }

    [Fact]
    public async Task REQ_RAT_157_29_February_is_a_normal_calendar_day_whichever_convention_is_used()
    {
        // A term bound on 29 Feb 2024 ends on 28 Feb 2025 (AddYears): 365 days.
        var start = new BusinessDate(2024, 2, 29);
        var end = start.AddYears(1);
        end.ShouldBe(new BusinessDate(2025, 2, 28));
        var oneDay = await Engine().RunAsync(Request(start, end, "TERM_RATIO", Both, Seg("S", start, start.AddDays(1), Line("L", 365.00m))));
        oneDay.TermDays.ShouldBe(365);
        oneDay.Lines.Single().Days.ShouldBe(1);
        oneDay.Lines.Single().Amount.Amount.ShouldBe(1.00m);

        // A 366-day term containing 29 Feb: the 2 days 28 Feb and 29 Feb.
        var t0 = new BusinessDate(2023, 3, 1);
        var t1 = new BusinessDate(2024, 3, 1);
        var feb28 = new BusinessDate(2024, 2, 28);
        var termRatio = await Engine().RunAsync(Request(t0, t1, "TERM_RATIO", Both, Seg("S", feb28, t1, Line("L", 366.00m))));
        termRatio.Lines.Single().Days.ShouldBe(2);
        termRatio.Lines.Single().Amount.Amount.ShouldBe(2.00m);
        var act = await Engine().RunAsync(Request(t0, t1, "ACT/365F", Both, Seg("S", feb28, t1, Line("L", 365.00m))));
        act.Lines.Single().Amount.Amount.ShouldBe(2.00m);

        // The 29th itself: [29 Feb, 1 Mar) is one day.
        var feb29 = await Engine().RunAsync(Request(t0, t1, "TERM_RATIO", Both, Seg("S", new BusinessDate(2024, 2, 29), t1, Line("L", 366.00m))));
        feb29.Lines.Single().Days.ShouldBe(1);
    }

    [Fact]
    public async Task REQ_RAT_157_days_are_whole_calendar_dates_so_the_daylight_saving_change_adds_no_hour()
    {
        // Athens clocks go forward on 2026-03-29; whole dates make [28 Mar, 30 Mar) exactly 2 days.
        var result = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly,
            Seg("S", new BusinessDate(2026, 3, 28), new BusinessDate(2026, 3, 30), Line("L", 365.00m))));

        result.Lines.Single().Days.ShouldBe(2);
        result.Lines.Single().Amount.Amount.ShouldBe(2.00m);
    }

    [Fact]
    public async Task A_zero_day_segment_is_worth_0_00_and_a_zero_day_term_is_refused()
    {
        var zero = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S", T0, T0, Line("L", 430.00m))));
        zero.Lines.Single().Days.ShouldBe(0);
        zero.Lines.Single().Amount.Amount.ShouldBe(0m);
        zero.Lines.Single().Residual.Amount.ShouldBe(0m);
        zero.Reverse().Lines.Single().Amount.Amount.ShouldBe(0m); // reversing nothing stays nothing

        var ex = await Should.ThrowAsync<DomainException>(() => Engine().RunAsync(
            Request(T0, T0, "TERM_RATIO", TermRatioOnly, Seg("S", T0, T0, Line("L", 430.00m)))));
        ex.Error.Code.Value.ShouldBe("RAT-ERR-PERIOD");
    }

    [Fact]
    public async Task A_full_term_segment_is_exactly_the_annual_rate_and_the_rounding_residual_is_returned()
    {
        var result = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S", T0, T1, Line("L", 123.4567m))));

        var line = result.Lines.Single();
        line.Unrounded.ShouldBe(123.4567m);
        line.Amount.Amount.ShouldBe(123.46m);
        line.Residual.Amount.ShouldBe(-0.0033m);
        line.Fraction.ShouldBe(1m);
    }

    [Theory]
    [InlineData(-5, 10)] // starts before the term
    [InlineData(10, 400)] // ends after the term
    [InlineData(10, 5)] // inverted
    public async Task REQ_RAT_157_a_segment_outside_the_term_or_inverted_is_RAT_ERR_PERIOD(int fromDays, int toDays)
    {
        var ex = await Should.ThrowAsync<DomainException>(() => Engine().RunAsync(
            Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S", T0.AddDays(fromDays), T0.AddDays(toDays), Line("L", 365m)))));

        ex.Error.Code.Value.ShouldBe("RAT-ERR-PERIOD");
    }

    [Fact]
    public async Task REQ_RAT_160_flat_and_fully_earned_charges_are_never_prorated()
    {
        var result = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly,
            Seg("S", T0, T0.AddDays(10),
                new ProrationLineInput("fee", "V1", "FEE", 25.00m, ProrationHandling.Flat),
                new ProrationLineInput("earned", "V1", "EARNED", 7.50m, ProrationHandling.FullyEarned),
                Line("prem", 365m))));

        result.Lines.Select(l => l.Amount.Amount).ShouldBe([25.00m, 7.50m, 10.00m]);
        result.Lines[0].Fraction.ShouldBe(1m);
    }

    [Fact]
    public async Task Minimum_premium_is_not_applied_here_a_tiny_prorated_amount_rounds_to_zero_and_keeps_its_residual()
    {
        // D-SL3-04: minimum premium and short-rate are not in the slice. 0.10 x 1 / 365 = 0.000273972... -> 0.00.
        var result = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S", T0, T0.AddDays(1), Line("L", 0.10m))));

        var line = result.Lines.Single();
        line.Amount.Amount.ShouldBe(0m);
        line.Residual.Amount.ShouldBe(line.Unrounded);
        line.Unrounded.ShouldBeGreaterThan(0m);
    }

    [Fact]
    public async Task REQ_RAT_163_a_half_cent_rounds_away_from_zero_in_both_directions_so_the_reversal_is_the_exact_negative()
    {
        // 1.825 x 1 / 365 = 0.005 exactly: the HALF_UP tie, for a debit and for a credit.
        var debit = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S", T0, T0.AddDays(1), Line("L", 1.825m))));
        var credit = await Engine().RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S", T0, T0.AddDays(1), Line("L", -1.825m))));

        debit.Lines.Single().Unrounded.ShouldBe(0.005m);
        debit.Lines.Single().Amount.Amount.ShouldBe(0.01m);
        debit.Lines.Single().Residual.Amount.ShouldBe(-0.005m);
        credit.Lines.Single().Amount.Amount.ShouldBe(-0.01m);
        credit.Lines.Single().Residual.Amount.ShouldBe(0.005m);
        credit.Lines.Single().ShouldBe(debit.Reverse().Lines.Single(), "the credit of a rate is the reversal of its debit");
    }

    [Fact]
    public async Task REQ_RAT_163_REQ_RAT_158_every_split_day_reverses_exactly_and_the_parts_add_up_to_the_term()
    {
        var engine = Engine();
        foreach (var rate in new[] { 430.00m, 123.4567m, 999.9999m, 0.0100m, 1000m })
        {
            for (var split = 0; split <= 365; split += 7)
            {
                var mid = T0.AddDays(split);
                var request = Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("A", T0, mid, Line("L", rate)), Seg("B", mid, T1, Line("L", rate)));
                var forward = await engine.RunAsync(request);
                var back = forward.Reverse();

                for (var i = 0; i < 2; i++)
                {
                    (forward.Lines[i].Amount + back.Lines[i].Amount).IsZero.ShouldBeTrue();
                    (forward.Lines[i].Residual + back.Lines[i].Residual).IsZero.ShouldBeTrue();
                    (forward.Lines[i].Amount.Amount + forward.Lines[i].Residual.Amount).ShouldBe(forward.Lines[i].Unrounded);
                }

                // the credit computed from the negated rate is the same as the reversal
                var credit = await engine.RunAsync(request with
                {
                    Segments = [.. request.Segments.Select(s => s with { Lines = [.. s.Lines.Select(l => l with { AnnualRate = -l.AnnualRate })] })],
                });
                credit.Lines.Select(l => l.Amount).ShouldBe(back.Lines.Select(l => l.Amount));

                // contiguous segments add up to the annual rate (within the 16-place truncation of each unrounded part)
                Math.Abs(forward.Lines.Sum(l => l.Unrounded) - rate).ShouldBeLessThan(1e-14m);
            }
        }
    }

    [Fact]
    public async Task The_configuration_hash_is_echoed_and_the_rounding_rule_is_reported()
    {
        var hash = ConfigurationHash.Parse(new string('a', 64));
        var request = Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S", T0, T0.AddDays(100), Line("L", 365.5m))) with { ConfigurationHash = hash };

        var result = await Engine().RunAsync(request);

        result.ConfigurationHash.ShouldBe(hash);
        result.Lines.Single().RoundingRuleKey.ShouldBe("cur.rounding.charge.line");
        result.RoundingLegalStatus.ShouldBe("NotRegulatory");
        result.Provisional.ShouldBeFalse();
    }

    [Fact]
    public async Task Bad_input_is_refused_not_guessed()
    {
        var engine = Engine();
        (await Should.ThrowAsync<DomainException>(() => engine.RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly)))).Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
        (await Should.ThrowAsync<DomainException>(() => engine.RunAsync(Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S", T0, T1))))).Error.Code.Value.ShouldBe("RAT-ERR-INPUT");
        // a rate with more digits than a decimal can multiply exactly is refused, never silently rounded
        var ex = await Should.ThrowAsync<DomainException>(() => engine.RunAsync(
            Request(T0, T1, "TERM_RATIO", TermRatioOnly, Seg("S", T0, T0.AddDays(3), Line("L", 7922816251426433759354395.0m)))));
        ex.Error.Code.Value.ShouldBe("RAT-ERR-SCALE");
    }

    // ---- fixtures -------------------------------------------------------------------------------------------------------------

    private static ProrationRequest Request(
        BusinessDate from, BusinessDate to, string convention, IReadOnlyList<string> declared, params ProrationSegmentInput[] segments) =>
        new(Eur, from, to, convention, declared, segments);

    private static ProrationSegmentInput Seg(string id, BusinessDate from, BusinessDate to, params ProrationLineInput[] lines) => new(id, from, to, lines);

    private static ProrationLineInput Line(string id, decimal annual) => new(id, "V1", "PREM-MTPL", annual);

    internal static RatingProrationEngine Engine()
    {
        var entity = new LegalEntityInfo(
            new LegalEntityId(Guid.Parse("0192f0c4-0000-7000-8000-000000000001")), LegalEntityCode.Parse("GR-TEST"), "GR", "gr", "EUR", "Europe/Athens", "ACTIVE", true);
        var clock = new ManualClock(Instant.FromUtc(2026, 10, 7));
        var catalogue = new ConfigurationCatalogue([new GrPackConfiguration()], clock.Now);
        var market = new ConfigurationEngine(catalogue, new LegalEntityRegistry([entity]), new Env("Development"), clock);
        return new RatingProrationEngine(new MarketRoundingService(market), new RequestContext { LegalEntity = LegalEntityCode.Parse("GR-TEST") });
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

internal static class ProrationTestExtensions
{
    public static Task<ProrationResult> RunAsync(this IRatingProrationEngine engine, ProrationRequest request) =>
        engine.ProrateAsync(request, TestContext.Current.CancellationToken);

    public static Task<ServicingTaxLinesResult> LinesAsync(this IRatingServicingTax service, ServicingTaxLinesRequest request) =>
        service.TaxLinesAsync(request, TestContext.Current.CancellationToken);
}
