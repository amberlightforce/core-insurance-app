namespace CoreIns.Rules.Tests;

/// <summary>NX-EC-06: the largest-remainder helper sums exactly and never strays more than one cent from the exact share.</summary>
public class CentAllocationTests
{
    [Fact]
    public void Allocate_sums_exactly_and_stays_within_one_cent_for_random_totals_including_negative()
    {
        var r = new Random(2606);
        for (var i = 0; i < 2000; i++)
        {
            var total = (r.Next(-2_000_000, 2_000_000)) / 100m;
            var weights = Enumerable.Range(0, r.Next(1, 9)).Select(_ => r.Next(0, 1000) / 7m).ToList();
            if (weights.Sum() == 0m)
            {
                weights[0] = 1m;
            }

            var parts = CentAllocation.Allocate(total, weights);
            Assert.Equal(total, parts.Sum());
            var sum = weights.Sum();
            for (var k = 0; k < parts.Length; k++)
            {
                Assert.True(Math.Abs(parts[k] - (total * weights[k] / sum)) <= 0.01m, $"iteration {i} part {k}");
                Assert.Equal(0m, parts[k] * 100m % 1m);
            }
        }
    }

    [Fact]
    public void Negative_total_is_split_not_skipped()
    {
        var parts = CentAllocation.Allocate(-0.10m, [1m, 1m, 1m]);
        Assert.Equal(-0.10m, parts.Sum());
        Assert.All(parts, p => Assert.True(p < 0m));
    }

    [Fact]
    public void Ties_go_to_the_lower_index()
    {
        Assert.Equal([0.34m, 0.33m, 0.33m], CentAllocation.Allocate(1.00m, [1m, 1m, 1m]));
    }

    [Fact]
    public void Distribute_keeps_the_total_with_mixed_sign_exacts()
    {
        var r = new Random(7);
        for (var i = 0; i < 1000; i++)
        {
            var exacts = Enumerable.Range(0, r.Next(1, 6)).Select(_ => r.Next(-100000, 100000) / 1000m).ToList();
            var total = DecimalRounding.RoundTo(exacts.Sum(), 2, RoundingMode.HalfUp);
            var parts = CentAllocation.Distribute(total, exacts);
            Assert.Equal(total, parts.Sum());
            for (var k = 0; k < parts.Length; k++)
            {
                Assert.True(Math.Abs(parts[k] - exacts[k]) <= 0.011m, $"iteration {i} part {k}");
            }
        }
    }

    [Fact]
    public void Invalid_weights_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => CentAllocation.Allocate(1m, []));
        Assert.Throws<ArgumentException>(() => CentAllocation.Allocate(1m, [0m, 0m]));
        Assert.Throws<ArgumentException>(() => CentAllocation.Allocate(1m, [-1m, 2m]));
    }
}
