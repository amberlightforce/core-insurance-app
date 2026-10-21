using CoreIns.Rules;
using CoreIns.Modules.Reinsurance.Cession;

namespace CoreIns.IntegrationTests.Reinsurance.Cession;

/// <summary>NX-EC-06: quota-share targets of one term are rounded jointly (largest remainder).</summary>
public sealed class JointTargetTests
{
    [Fact]
    public void Targets_sum_to_the_rounded_total_and_pin_the_changed_boundary_case()
    {
        // Two treaties either side of a boundary, 50% of 0.01 each: independent rounding ceded 0.01 + 0.01 = 0.02, now 0.01 in total.
        var targets = CessionEngine.JointTargets([(50m, 0.01m), (50m, 0.01m)]);
        targets.ShouldBe([0.01m, 0.00m]);
    }

    [Fact]
    public void Single_treaty_matches_the_plain_target()
    {
        var r = new Random(26);
        for (var i = 0; i < 500; i++)
        {
            var share = r.Next(0, 10001) / 100m;
            var premium = r.Next(-500000, 500000) / 100m;
            CessionEngine.JointTargets([(share, premium)])[0].ShouldBe(CessionEngine.Target(share, premium));
        }
    }

    [Fact]
    public void Multi_treaty_property_sum_exact_within_one_cent()
    {
        var r = new Random(266);
        for (var i = 0; i < 1000; i++)
        {
            var input = Enumerable.Range(0, r.Next(1, 5)).Select(_ => (r.Next(1, 10001) / 100m, r.Next(-500000, 500000) / 100m)).ToList();
            var targets = CessionEngine.JointTargets(input);
            var exacts = input.Select(t => t.Item1 / 100m * t.Item2).ToList();
            targets.Sum().ShouldBe(DecimalRounding.RoundTo(exacts.Sum(), 2, DecimalRounding.DefaultMoneyMode));
            for (var k = 0; k < targets.Length; k++)
            {
                Math.Abs(targets[k] - exacts[k]).ShouldBeLessThanOrEqualTo(0.011m);
            }
        }
    }
}
