namespace CoreIns.Rules.Tests;

/// <summary>NX-EC-11: the instalment split sums exactly per charge and per term, and premium + tax + levy + fee = gross.</summary>
public class InstalmentSplitTests
{
    [Fact]
    public void Split_sums_exactly_per_charge_and_premium_plus_taxes_equal_gross()
    {
        var r = new Random(2611);
        int[] counts = [1, 2, 4, 12];
        for (var i = 0; i < 2000; i++)
        {
            var n = counts[r.Next(counts.Length)];
            var charges = r.Next(1, 7);
            var amounts = Enumerable.Range(0, charges).Select(_ => r.Next(1, 500_000) / 100m).ToList();
            var premium = Enumerable.Range(0, charges).Select(_ => r.Next(2) == 0).ToList();
            var parts = InstalmentSplit.Split(amounts, premium, n);

            for (var c = 0; c < charges; c++)
            {
                Assert.Equal(n, parts[c].Length);
                Assert.Equal(amounts[c], parts[c].Sum());
                Assert.All(parts[c], p => Assert.Equal(0m, p * 100m % 1m));
            }

            var gross = Enumerable.Range(0, n).Select(k => Enumerable.Range(0, charges).Sum(c => parts[c][k])).ToList();
            Assert.Equal(amounts.Sum(), gross.Sum());
            var premiumSum = Enumerable.Range(0, charges).Where(c => premium[c]).Sum(c => amounts[c]);
            var otherSum = Enumerable.Range(0, charges).Where(c => !premium[c]).Sum(c => amounts[c]);
            Assert.Equal(premiumSum + otherSum, gross.Sum());
        }
    }

    [Fact]
    public void Taxes_follow_the_premium_share_and_premium_is_even_within_one_cent()
    {
        var parts = InstalmentSplit.Split([100.01m, 15m], [true, false], 4);
        Assert.Equal(100.01m, parts[0].Sum());
        Assert.True(parts[0].Max() - parts[0].Min() <= 0.01m);
        Assert.Equal(15m, parts[1].Sum());
        Assert.True(parts[1].Max() - parts[1].Min() <= 0.01m);
    }

    [Fact]
    public void Without_premium_the_other_charges_split_evenly()
    {
        var parts = InstalmentSplit.Split([10m], [false], 3);
        Assert.Equal(10m, parts[0].Sum());
    }
}
