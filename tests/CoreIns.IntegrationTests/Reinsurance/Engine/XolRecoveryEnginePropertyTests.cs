using CoreIns.Modules.Reinsurance.Domain.Recovery;

namespace CoreIns.IntegrationTests.Reinsurance.Engine;

/// <summary>Seeded property tests of the XoL engine (hand-rolled generator; deterministic).</summary>
public sealed class XolRecoveryEnginePropertyTests
{
    private const int Iterations = 400;

    private static decimal Amt(Random r, int maxUnits) => r.Next(0, maxUnits) * 1000m + r.Next(0, 100) / 100m;

    private static ContractTerms RandomTerms(Random r)
    {
        var layers = new List<XolLayer>();
        var attach = r.Next(0, 3) * 100_000m;
        var n = r.Next(1, 4);
        for (var i = 0; i < n; i++)
        {
            var limit = r.Next(0, 5) == 0 ? (decimal?)null : r.Next(1, 8) * 100_000m;
            var aad = r.Next(0, 2) == 0 ? 0m : r.Next(0, 6) * 50_000m;
            var aal = r.Next(0, 2) == 0 ? (decimal?)null : r.Next(1, 8) * 100_000m;
            layers.Add(new XolLayer("L" + i, attach, limit, aad, aal));
            attach += limit ?? 1_000_000m;
        }

        var lines = r.Next(0, 3) switch { 0 => new[] { 0.60m, 0.40m }, 1 => [0.5m, 0.3m, 0.2m], _ => [0.333m, 0.333m, 0.334m] };
        var parts = lines.Select((l, i) => new Participation("P" + i, l, i == 0)).ToList();
        var placed = new[] { 1m, 0.9m, 0.75m, 0.333m }[r.Next(0, 4)];
        return new ContractTerms(layers, new UnlClause(r.Next(0, 2) == 0, r.Next(0, 2) == 0), parts, placed, XolRecoveryEngineTests.Cents);
    }

    private static List<RecoveryOccurrence> RandomOccurrences(Random r, int count) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var closed = r.Next(0, 4) == 0;
            var c = new ClaimAmounts(
                Amt(r, 600), closed ? 0m : Amt(r, 800), Amt(r, 50), closed ? 0m : Amt(r, 50), Amt(r, 10), 0m,
                r.Next(0, 3) == 0 ? Amt(r, 100) : 0m, Amt(r, 30), closed);
            return new RecoveryOccurrence("O" + r.Next(0, 1_000_000).ToString("D7", System.Globalization.CultureInfo.InvariantCulture) + i, new DateOnly(2028, 1, 1).AddDays(r.Next(0, 40)), c);
        }).ToList();

    private static RecoveryResult Run(ContractTerms t, IReadOnlyList<RecoveryOccurrence> o, IReadOnlyList<RecoverableRow> b)
    {
        var outcome = XolRecoveryEngine.Calculate(new RecoveryInput(t, o, b));
        outcome.Errors.ShouldBeEmpty();
        return outcome.Result!;
    }

    [Fact]
    public void REQ_RI_129_idempotent_rerun_books_no_delta()
    {
        var r = new Random(1129);
        for (var i = 0; i < Iterations; i++)
        {
            var terms = RandomTerms(r);
            var occ = RandomOccurrences(r, r.Next(0, 9));
            var first = Run(terms, occ, []);
            Run(terms, occ, first.Targets).Deltas.ShouldBeEmpty($"iteration {i}");
        }
    }

    [Fact]
    public void REQ_RI_129_order_independent_over_shuffled_occurrences()
    {
        var r = new Random(2129);
        for (var i = 0; i < Iterations; i++)
        {
            var terms = RandomTerms(r);
            var occ = RandomOccurrences(r, r.Next(1, 9));
            var a = Run(terms, occ, []);
            var shuffled = occ.OrderBy(_ => r.Next()).ToList();
            var b = Run(terms, shuffled, []);
            b.Targets.ShouldBe(a.Targets, $"iteration {i}");
            b.Traces.ShouldBe(a.Traces, $"iteration {i}");
        }
    }

    [Fact]
    public void Participants_sum_exactly_to_layer_recoverable_times_placed_pct()
    {
        var r = new Random(3129);
        for (var i = 0; i < Iterations; i++)
        {
            var terms = RandomTerms(r);
            var res = Run(terms, RandomOccurrences(r, r.Next(1, 9)), []);
            foreach (var t in res.Traces)
            {
                var rows = res.Targets.Where(x => x.OccurrenceId == t.OccurrenceId && x.LayerId == t.LayerId).ToList();
                rows.Sum(x => x.Incurred).ShouldBe(XolRecoveryEngineTests.Cents(t.RecoverableIncurred * terms.PlacedPct));
                rows.Sum(x => x.Paid).ShouldBe(XolRecoveryEngineTests.Cents(t.RecoverablePaid * terms.PlacedPct));
                rows.Sum(x => x.Outstanding).ShouldBe(rows.Sum(x => x.Incurred) - rows.Sum(x => x.Paid));
            }
        }
    }

    [Fact]
    public void Recoveries_never_exceed_limit_or_aggregate_limit_and_are_never_negative()
    {
        var r = new Random(4129);
        for (var i = 0; i < Iterations; i++)
        {
            var terms = RandomTerms(r);
            var res = Run(terms, RandomOccurrences(r, r.Next(1, 9)), []);
            foreach (var t in res.Traces)
            {
                t.RecoverableIncurred.ShouldBeGreaterThanOrEqualTo(0m);
                t.RecoverablePaid.ShouldBeGreaterThanOrEqualTo(0m);
                if (t.Limit is { } limit)
                {
                    t.LayerLossIncurred.ShouldBeLessThanOrEqualTo(limit);
                    t.RecoverableIncurred.ShouldBeLessThanOrEqualTo(limit);
                }
            }

            foreach (var layer in res.Traces.GroupBy(t => t.LayerId))
            {
                var aal = layer.First().Aal;
                if (aal is { } cap)
                {
                    layer.Sum(t => t.RecoverableIncurred).ShouldBeLessThanOrEqualTo(cap);
                    layer.Sum(t => t.RecoverablePaid).ShouldBeLessThanOrEqualTo(cap);
                }
            }
        }
    }

    [Fact]
    public void Paid_recoverable_never_exceeds_incurred_recoverable_over_the_year_and_per_occurrence_without_aggregates()
    {
        var r = new Random(5129);
        for (var i = 0; i < Iterations; i++)
        {
            var terms = RandomTerms(r);
            var res = Run(terms, RandomOccurrences(r, r.Next(1, 9)), []);
            foreach (var layer in res.Traces.GroupBy(t => t.LayerId))
            {
                layer.Sum(t => t.RecoverablePaid).ShouldBeLessThanOrEqualTo(layer.Sum(t => t.RecoverableIncurred) + 0m);
                if (layer.First() is { Aad: 0m, Aal: null })
                {
                    layer.ShouldAllBe(t => t.RecoverablePaid <= t.RecoverableIncurred);
                }
            }
        }
    }

    [Fact]
    public void Deltas_summed_over_successive_runs_equal_the_final_target()
    {
        var r = new Random(6129);
        for (var i = 0; i < Iterations / 4; i++)
        {
            var terms = RandomTerms(r);
            var occ = RandomOccurrences(r, r.Next(1, 7));
            var booked = new Dictionary<(string, string, string), RecoverableRow>();
            for (var step = 0; step < 5; step++)
            {
                occ = occ.Select(o => r.Next(0, 2) == 0 ? o : o with { Claim = o.Claim with { IndemnityOpen = o.Claim.Closed ? 0m : Amt(r, 900), IndemnityPaid = o.Claim.IndemnityPaid + Amt(r, 20) } }).ToList();
                var res = Run(terms, occ, [.. booked.Values]);
                foreach (var d in res.Deltas)
                {
                    var k = (d.OccurrenceId, d.LayerId, d.ParticipantId);
                    booked.TryGetValue(k, out var cur);
                    booked[k] = new RecoverableRow(d.OccurrenceId, d.LayerId, d.ParticipantId, (cur?.Incurred ?? 0m) + d.Incurred, (cur?.Paid ?? 0m) + d.Paid, (cur?.Outstanding ?? 0m) + d.Outstanding);
                }

                var final = Run(terms, occ, []);
                foreach (var t in final.Targets)
                {
                    var b = booked.GetValueOrDefault((t.OccurrenceId, t.LayerId, t.ParticipantId));
                    (b?.Incurred ?? 0m).ShouldBe(t.Incurred);
                    (b?.Paid ?? 0m).ShouldBe(t.Paid);
                    (b?.Outstanding ?? 0m).ShouldBe(t.Outstanding);
                }
            }
        }
    }
}
