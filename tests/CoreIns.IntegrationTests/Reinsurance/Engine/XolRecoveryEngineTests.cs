using CoreIns.Modules.Reinsurance.Domain.Recovery;

namespace CoreIns.IntegrationTests.Reinsurance.Engine;

/// <summary>Golden tests of the pure XoL recovery engine (D-SL4-05, SLICE-PLAN-4 §3.1). No database.</summary>
public sealed class XolRecoveryEngineTests
{
    internal static decimal Cents(decimal x) => decimal.Round(x, 2, MidpointRounding.AwayFromZero);

    internal static readonly XolLayer Gt05 = new("L1", 250_000m, 500_000m);

    internal static ContractTerms Terms(params XolLayer[] layers) => Terms(new UnlClause(true, false), 1m, layers);

    internal static ContractTerms Terms(UnlClause clause, decimal placed, params XolLayer[] layers) =>
        new(layers.Length == 0 ? [Gt05] : layers, clause, [new Participation("RE-A", 0.60m, true), new Participation("RE-B", 0.40m, false)], placed, Cents, "RI-TEST-v1");

    internal static ClaimAmounts Claim(decimal paid = 0m, decimal open = 0m, decimal alaePaid = 0m, decimal alaeOpen = 0m, decimal realised = 0m, bool closed = false, decimal interestPaid = 0m, decimal recReserve = 0m) =>
        new(paid, open, alaePaid, alaeOpen, interestPaid, 0m, realised, recReserve, closed);

    internal static RecoveryOccurrence Occ(string id, string date, ClaimAmounts c) => new(id, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), c);

    internal static RecoveryResult Run(ContractTerms terms, IReadOnlyList<RecoveryOccurrence> occ, IReadOnlyList<RecoverableRow>? booked = null)
    {
        var outcome = XolRecoveryEngine.Calculate(new RecoveryInput(terms, occ, booked ?? []));
        outcome.Errors.ShouldBeEmpty();
        return outcome.Result!;
    }

    private static decimal Sum(IEnumerable<RecoverableRow> rows, Func<RecoverableRow, decimal> f) => rows.Sum(f);

    [Fact]
    public void GT_05_UNL_path_300k_900k_700k_gives_deltas_plus50k_plus450k_minus50k()
    {
        var terms = Terms();
        var r1 = Run(terms, [Occ("C1", "2028-03-03", Claim(open: 300_000m))]);
        Sum(r1.Targets, r => r.Incurred).ShouldBe(50_000m);
        Sum(r1.Deltas, r => r.Incurred).ShouldBe(50_000m);
        r1.Deltas.Single(d => d.ParticipantId == "RE-A").Incurred.ShouldBe(30_000m);
        r1.Deltas.Single(d => d.ParticipantId == "RE-B").Incurred.ShouldBe(20_000m);

        var r2 = Run(terms, [Occ("C1", "2028-03-03", Claim(open: 900_000m))], r1.Targets);
        Sum(r2.Targets, r => r.Incurred).ShouldBe(500_000m);
        Sum(r2.Deltas, r => r.Incurred).ShouldBe(450_000m);

        var r3 = Run(terms, [Occ("C1", "2028-03-03", Claim(open: 500_000m, paid: 400_000m, realised: 200_000m))], r2.Targets);
        Sum(r3.Targets, r => r.Incurred).ShouldBe(450_000m);
        Sum(r3.Deltas, r => r.Incurred).ShouldBe(-50_000m);
    }

    [Fact]
    public void SLICE_PLAN_4_section_3_1_table_rows_a_to_d()
    {
        var terms = Terms();
        (ClaimAmounts claim, decimal inc, decimal paid, decimal outstanding)[] rows =
        [
            (Claim(open: 300_000m), 50_000m, 0m, 50_000m),
            (Claim(open: 900_000m), 500_000m, 0m, 500_000m),
            (Claim(paid: 400_000m, open: 500_000m), 500_000m, 150_000m, 350_000m),
            (Claim(paid: 400_000m, open: 500_000m, realised: 200_000m), 450_000m, 0m, 450_000m),
        ];
        IReadOnlyList<RecoverableRow> booked = [];
        decimal[] expectedDelta = [50_000m, 450_000m, 0m, -50_000m];
        for (var i = 0; i < rows.Length; i++)
        {
            var r = Run(terms, [Occ("C1", "2028-03-03", rows[i].claim)], booked);
            Sum(r.Targets, t => t.Incurred).ShouldBe(rows[i].inc, $"row {i} incurred");
            Sum(r.Targets, t => t.Paid).ShouldBe(rows[i].paid, $"row {i} paid");
            r.LayerYearTotals.Single(x => x.ParticipantId is null).Outstanding.ShouldBe(rows[i].outstanding, $"row {i} outstanding");
            Sum(r.Deltas, t => t.Incurred).ShouldBe(expectedDelta[i], $"row {i} delta");
            booked = r.Targets;
        }
    }

    [Fact]
    public void REQ_RI_119_incurred_900k_paid_400k_gives_500k_150k_350k()
    {
        var r = Run(Terms(), [Occ("C1", "2028-03-03", Claim(paid: 400_000m, open: 500_000m))]);
        var t = r.Traces.Single();
        (t.RecoverableIncurred, t.RecoverablePaid).ShouldBe((500_000m, 150_000m));
        r.LayerYearTotals.Single(x => x.ParticipantId is null).Outstanding.ShouldBe(350_000m);
    }

    [Fact]
    public void REQ_RI_123_two_layers_with_UNL_1_2m_give_500k_and_450k()
    {
        var r = Run(Terms(Gt05, new XolLayer("L2", 750_000m, 2_000_000m)), [Occ("C1", "2028-03-03", Claim(open: 1_200_000m))]);
        r.Traces.Single(t => t.LayerId == "L1").RecoverableIncurred.ShouldBe(500_000m);
        r.Traces.Single(t => t.LayerId == "L2").RecoverableIncurred.ShouldBe(450_000m);
    }

    [Fact]
    public void REQ_RI_123_unlimited_layer_has_no_cap()
    {
        var r = Run(Terms(new XolLayer("U", 250_000m, null)), [Occ("C1", "2028-03-03", Claim(open: 5_000_000m))]);
        r.Traces.Single().RecoverableIncurred.ShouldBe(4_750_000m);
    }

    [Fact]
    public void REQ_RI_125_AAD_300k_with_200k_then_250k_gives_0_and_150k()
    {
        var terms = Terms(new XolLayer("L1", 0m, null, Aad: 300_000m));
        var r = Run(terms, [Occ("O2", "2028-06-09", Claim(open: 250_000m)), Occ("O1", "2028-03-03", Claim(open: 200_000m))]);
        r.Traces.Single(t => t.OccurrenceId == "O1").RecoverableIncurred.ShouldBe(0m);
        var o2 = r.Traces.Single(t => t.OccurrenceId == "O2");
        o2.RecoverableIncurred.ShouldBe(150_000m);
        o2.AggregateIncurred.CumulativeBefore.ShouldBe(200_000m);
        o2.AggregateIncurred.CumulativeAfter.ShouldBe(450_000m);
    }

    [Fact]
    public void REQ_RI_125_same_date_orders_by_occurrence_id()
    {
        var terms = Terms(new XolLayer("L1", 0m, null, Aad: 100m));
        var r = Run(terms, [Occ("B", "2028-03-03", Claim(open: 100m)), Occ("A", "2028-03-03", Claim(open: 100m))]);
        r.Traces.Select(t => t.OccurrenceId).ShouldBe(["A", "B"]);
        r.Traces[1].RecoverableIncurred.ShouldBe(100m);
    }

    [Fact]
    public void REQ_RI_125_AAL_caps_the_aggregate_recovery()
    {
        var terms = Terms(new XolLayer("L1", 0m, null, Aad: 100m, Aal: 300m));
        var r = Run(terms, [Occ("A", "2028-01-01", Claim(open: 250m)), Occ("B", "2028-02-01", Claim(open: 250m))]);
        r.Traces.Sum(t => t.RecoverableIncurred).ShouldBe(300m);
    }

    [Fact]
    public void REQ_RI_126_earlier_occurrence_growing_restates_both()
    {
        var terms = Terms(new XolLayer("L1", 0m, null, Aad: 300_000m));
        var first = Run(terms, [Occ("O1", "2028-03-03", Claim(open: 200_000m)), Occ("O2", "2028-06-09", Claim(open: 250_000m))]);
        var second = Run(terms, [Occ("O1", "2028-03-03", Claim(open: 400_000m)), Occ("O2", "2028-06-09", Claim(open: 250_000m))], first.Targets);
        second.Traces.Single(t => t.OccurrenceId == "O1").RecoverableIncurred.ShouldBe(100_000m);
        second.Traces.Single(t => t.OccurrenceId == "O2").RecoverableIncurred.ShouldBe(250_000m);
        Sum(second.Deltas.Where(d => d.OccurrenceId == "O1"), d => d.Incurred).ShouldBe(100_000m);
        Sum(second.Deltas.Where(d => d.OccurrenceId == "O2"), d => d.Incurred).ShouldBe(100_000m);
    }

    [Fact]
    public void REQ_RI_116_indemnity_900k_plus_ALAE_60k_minus_salvage_40k_is_920k()
    {
        var c = Claim(open: 900_000m, alaeOpen: 60_000m, realised: 40_000m);
        var unl = XolRecoveryEngine.UnlOf(c, new UnlClause(true, false), incurred: true);
        unl.Unl.ShouldBe(920_000m);
        (unl.Indemnity, unl.Alae, unl.RealisedRecoveries).ShouldBe((900_000m, 60_000m, 40_000m));
        Run(Terms(), [Occ("C1", "2028-03-03", c)]).Traces.Single().UnlIncurred.Unl.ShouldBe(920_000m);
    }

    [Fact]
    public void REQ_RI_116_ALAE_and_interest_follow_the_clause()
    {
        var c = Claim(open: 100m, alaeOpen: 10m, interestPaid: 5m);
        XolRecoveryEngine.UnlOf(c, new UnlClause(false, false), true).Unl.ShouldBe(100m);
        XolRecoveryEngine.UnlOf(c, new UnlClause(true, false), true).Unl.ShouldBe(110m);
        XolRecoveryEngine.UnlOf(c, new UnlClause(true, true), true).Unl.ShouldBe(115m);
        XolRecoveryEngine.UnlOf(c, new UnlClause(true, true), false).Unl.ShouldBe(5m);
    }

    [Fact]
    public void REQ_RI_116_UNL_is_floored_at_zero()
    {
        XolRecoveryEngine.UnlOf(Claim(open: 100m, realised: 500m), new UnlClause(true, false), true).Unl.ShouldBe(0m);
    }

    [Fact]
    public void REQ_RI_117_open_recovery_reserve_is_ignored_unless_the_clause_says_it_inures()
    {
        var c = Claim(open: 400_000m, recReserve: 50_000m);
        XolRecoveryEngine.UnlOf(c, new UnlClause(true, false), true).Unl.ShouldBe(400_000m);
        XolRecoveryEngine.UnlOf(c, new UnlClause(true, false, AnticipatedRecoveriesInure: true), true).Unl.ShouldBe(350_000m);
        XolRecoveryEngine.UnlOf(c, new UnlClause(true, false, AnticipatedRecoveriesInure: true), false).Unl.ShouldBe(0m);
    }

    [Fact]
    public void REQ_RI_122_closed_claim_has_zero_outstanding_and_unchanged_paid()
    {
        var open = Run(Terms(), [Occ("C1", "2028-03-03", Claim(paid: 400_000m, open: 500_000m))]);
        var closed = Run(Terms(), [Occ("C1", "2028-03-03", Claim(paid: 400_000m, closed: true))], open.Targets);
        var t = closed.Traces.Single();
        t.RecoverableIncurred.ShouldBe(t.RecoverablePaid);
        t.RecoverablePaid.ShouldBe(150_000m);
        closed.LayerYearTotals.Single(x => x.ParticipantId is null).Outstanding.ShouldBe(0m);
        Sum(closed.Deltas, r => r.Paid).ShouldBe(0m);
        Sum(closed.Deltas, r => r.Incurred).ShouldBe(-350_000m);
    }

    [Fact]
    public void REQ_RI_122_closed_claim_with_an_open_reserve_is_refused_not_clamped()
    {
        var o = XolRecoveryEngine.Calculate(new RecoveryInput(Terms(), [Occ("C1", "2028-03-03", Claim(paid: 1m, open: 1m, closed: true))], []));
        o.IsSuccess.ShouldBeFalse();
        o.Errors.Select(e => e.Code).ShouldContain("CLOSED_WITH_OPEN_RESERVE");
    }

    [Theory]
    [InlineData("0.01", "0.01", "0.00")]
    [InlineData("0.05", "0.03", "0.02")]
    [InlineData("0.03", "0.02", "0.01")]
    [InlineData("1000.01", "600.01", "400.00")]
    public void Split_60_40_in_cents_gives_the_residual_to_the_lead(string total, string lead, string other)
    {
        var amount = decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture) + 250_000m;
        var r = Run(Terms(), [Occ("C1", "2028-03-03", Claim(open: amount))]);
        r.Targets.Single(t => t.ParticipantId == "RE-A").Incurred.ShouldBe(decimal.Parse(lead, System.Globalization.CultureInfo.InvariantCulture));
        r.Targets.Single(t => t.ParticipantId == "RE-B").Incurred.ShouldBe(decimal.Parse(other, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Placed_percentage_scales_the_recoverable_and_the_split_still_sums()
    {
        var r = Run(Terms(new UnlClause(true, false), 0.75m, Gt05), [Occ("C1", "2028-03-03", Claim(open: 900_000m))]);
        Sum(r.Targets, t => t.Incurred).ShouldBe(375_000m);
    }

    [Fact]
    public void REQ_RI_129_rerun_with_booked_equal_to_target_yields_no_delta()
    {
        var occ = new[] { Occ("C1", "2028-03-03", Claim(paid: 400_000m, open: 500_000m)), Occ("C2", "2028-04-03", Claim(open: 333_333.33m)) };
        var first = Run(Terms(), occ);
        Run(Terms(), occ, first.Targets).Deltas.ShouldBeEmpty();
    }

    [Fact]
    public void REQ_RI_127_a_claim_that_vanishes_from_the_year_is_reversed_by_negative_deltas()
    {
        var first = Run(Terms(), [Occ("C1", "2028-03-03", Claim(open: 900_000m))]);
        var second = Run(Terms(), [], first.Targets);
        Sum(second.Deltas, d => d.Incurred).ShouldBe(-500_000m);
    }

    [Fact]
    public void REQ_RI_132_trace_carries_inputs_unl_attachment_limit_aggregate_outputs_and_engine_version()
    {
        var c = Claim(open: 900_000m, alaeOpen: 60_000m, realised: 10_000m);
        var t = Run(Terms(), [Occ("C1", "2028-03-03", c)]).Traces.Single();
        t.Inputs.ShouldBe(c);
        t.UnlIncurred.Unl.ShouldBe(950_000m);
        (t.Attachment, t.Limit).ShouldBe((250_000m, (decimal?)500_000m));
        t.LayerLossIncurred.ShouldBe(500_000m);
        t.AggregateIncurred.CumulativeAfter.ShouldBe(500_000m);
        t.RecoverableIncurred.ShouldBe(500_000m);
        t.EngineVersion.ShouldBe(XolRecoveryEngine.EngineVersion);
        t.EngineVersion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Zero_claim_is_valid_and_yields_zero_without_throwing()
    {
        var r = Run(Terms(), [Occ("C1", "2028-03-03", Claim())]);
        Sum(r.Targets, t => t.Incurred).ShouldBe(0m);
        r.Deltas.ShouldBeEmpty();
    }

    [Fact]
    public void Impossible_inputs_return_typed_errors()
    {
        ContractTerms Bad(XolLayer l) => Terms(l);
        string[] Codes(ContractTerms t) => XolRecoveryEngine.Calculate(new RecoveryInput(t, [], [])).Errors.Select(e => e.Code).ToArray();

        Codes(Bad(new XolLayer("L", 0m, -1m))).ShouldContain("LIMIT_NEGATIVE");
        Codes(Bad(new XolLayer("L", -1m, 1m))).ShouldContain("ATTACHMENT_NEGATIVE");
        Codes(Bad(new XolLayer("L", 0m, 1m, Aad: -1m))).ShouldContain("AAD_NEGATIVE");
        Codes(Bad(new XolLayer("L", 0m, 1m, Aal: -1m))).ShouldContain("AAL_NEGATIVE");
        Codes(Terms() with { PlacedPct = 1.5m }).ShouldContain("PLACED_PCT_INVALID");
        Codes(Terms() with { Round = null! }).ShouldContain("ROUNDING_MISSING");
        Codes(Terms() with { Participations = [new Participation("A", 0.5m, true), new Participation("B", 0.4m, false)] }).ShouldContain("LINES_NOT_100");
        Codes(Terms() with { Participations = [new Participation("A", 0.5m, false), new Participation("B", 0.5m, false)] }).ShouldContain("LEAD_INVALID");
        Codes(Terms() with { Layers = [] }).ShouldContain("LAYERS_MISSING");
    }

    [Fact]
    public void Negative_amounts_duplicates_and_unknown_booked_keys_are_refused()
    {
        string[] Codes(IReadOnlyList<RecoveryOccurrence> o, IReadOnlyList<RecoverableRow>? b = null) =>
            XolRecoveryEngine.Calculate(new RecoveryInput(Terms(), o, b ?? [])).Errors.Select(e => e.Code).ToArray();

        Codes([Occ("C1", "2028-03-03", Claim(open: -1m))]).ShouldContain("AMOUNT_NEGATIVE");
        Codes([Occ("C1", "2028-03-03", Claim()), Occ("C1", "2028-03-04", Claim())]).ShouldContain("OCCURRENCE_DUPLICATE");
        Codes([], [new RecoverableRow("C1", "NOPE", "RE-A", 1m, 0m)]).ShouldContain("BOOKED_UNKNOWN_KEY");
    }

    [Fact]
    public void D1_split_never_gives_a_negative_share_lead_10_percent_three_30_percent_and_2_cents()
    {
        var terms = Terms() with
        {
            Participations = [new Participation("L", 0.10m, true), new Participation("A", 0.30m, false), new Participation("B", 0.30m, false), new Participation("C", 0.30m, false)],
        };
        var r = Run(terms, [Occ("C1", "2028-03-03", Claim(open: 250_000.02m))]);
        r.Targets.ShouldAllBe(t => t.Incurred >= 0m);
        Sum(r.Targets, t => t.Incurred).ShouldBe(0.02m);
        r.Targets.Single(t => t.ParticipantId == "L").Incurred.ShouldBe(0m);
    }

    [Fact]
    public void D1_split_ties_go_to_the_lead_then_participant_id()
    {
        var terms = Terms() with { Participations = [new Participation("B", 0.5m, false), new Participation("A", 0.5m, true)] };
        var r = Run(terms, [Occ("C1", "2028-03-03", Claim(open: 250_000.01m))]);
        r.Targets.Single(t => t.ParticipantId == "A").Incurred.ShouldBe(0.01m);
        r.Targets.Single(t => t.ParticipantId == "B").Incurred.ShouldBe(0m);
    }

    [Fact]
    public void D2_P2_closed_claim_on_an_AAD_layer_layer_year_outstanding_is_not_negative()
    {
        var terms = Terms(new XolLayer("L1", 0m, null, Aad: 100_000m));
        var r = Run(terms, [Occ("A", "2028-01-01", Claim(open: 300_000m)), Occ("B", "2028-02-01", Claim(paid: 80_000m, closed: true))]);
        r.LayerYearTotals.Single(x => x.ParticipantId is null).Outstanding.ShouldBe(280_000m);
        r.LayerYearTotals.ShouldAllBe(x => x.Outstanding >= 0m);
    }

    [Fact]
    public void D2_P3_AAL_case_layer_year_outstanding_is_not_negative()
    {
        var terms = Terms(new XolLayer("L1", 0m, null, Aal: 100_000m));
        var r = Run(terms, [Occ("A", "2028-01-01", Claim(open: 1_000_000m)), Occ("B", "2028-02-01", Claim(paid: 50_000m, open: 10_000m))]);
        var total = r.LayerYearTotals.Single(x => x.ParticipantId is null);
        total.Incurred.ShouldBe(100_000m);
        total.Paid.ShouldBe(50_000m);
        total.Outstanding.ShouldBe(50_000m);
    }

    [Fact]
    public void M1_open_recovery_reserve_above_the_open_reserve_is_refused()
    {
        var o = XolRecoveryEngine.Calculate(new RecoveryInput(Terms(), [Occ("C1", "2028-03-03", Claim(open: 10m, recReserve: 11m))], []));
        o.Errors.Select(e => e.Code).ShouldContain("OPEN_RECOVERY_RESERVE_EXCEEDS_OPEN");
    }

    [Fact]
    public void M2_null_elements_and_blank_ids_are_refused()
    {
        string[] Codes(RecoveryInput i) => XolRecoveryEngine.Calculate(i).Errors.Select(e => e.Code).ToArray();
        Codes(new RecoveryInput(Terms(), [null!], [])).ShouldContain("ELEMENT_NULL");
        Codes(new RecoveryInput(Terms(), [], [null!])).ShouldContain("ELEMENT_NULL");
        Codes(new RecoveryInput(Terms() with { Layers = [null!] }, [], [])).ShouldContain("ELEMENT_NULL");
        Codes(new RecoveryInput(Terms() with { Participations = [new Participation(" ", 1m, true)] }, [], [])).ShouldContain("PARTICIPANT_ID_MISSING");
        Codes(new RecoveryInput(Terms() with { ContractVersion = "" }, [], [])).ShouldContain("CONTRACT_VERSION_MISSING");
    }

    [Fact]
    public void M3_trace_echoes_clause_flags_and_contract_version()
    {
        var t = Run(Terms(new UnlClause(true, true, true), 1m), [Occ("C1", "2028-03-03", Claim(open: 400_000m))]).Traces.Single();
        (t.ClauseIncludeAlae, t.ClauseIncludeStatutoryInterest, t.ClauseAnticipatedRecoveriesInure, t.ContractVersion).ShouldBe((true, true, true, "RI-TEST-v1"));
    }
}
