namespace CoreIns.Rules;

/// <summary>
/// NX-EC-11: splits a term's charges over N instalments. Premium charges are split evenly by largest remainder
/// (<see cref="CentAllocation"/>); every non-premium charge (tax, levy, fee) is split pro rata to each instalment's premium
/// share, again by largest remainder. Each charge's parts sum exactly to the charge, so per instalment premium + tax + levy
/// + fee is the instalment's gross, and the instalments' gross sums exactly to the term's gross.
/// </summary>
public static class InstalmentSplit
{
    /// <summary>
    /// Returns <c>result[charge][instalment]</c>. <paramref name="isPremium"/> marks the premium charges; when the instalments
    /// have no premium to weigh by (no premium charge, or all zero) the other charges are split evenly.
    /// </summary>
    public static decimal[][] Split(IReadOnlyList<decimal> amounts, IReadOnlyList<bool> isPremium, int instalments)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        ArgumentNullException.ThrowIfNull(isPremium);
        if (instalments < 1 || amounts.Count != isPremium.Count)
        {
            throw new ArgumentException("At least one instalment and one premium flag per charge are required.");
        }

        var even = Enumerable.Repeat(1m, instalments).ToArray();
        var result = new decimal[amounts.Count][];
        var premiumPerInstalment = new decimal[instalments];
        for (var c = 0; c < amounts.Count; c++)
        {
            if (!isPremium[c])
            {
                continue;
            }

            result[c] = CentAllocation.Allocate(amounts[c], even);
            for (var i = 0; i < instalments; i++)
            {
                premiumPerInstalment[i] += Math.Abs(result[c][i]);
            }
        }

        var weights = premiumPerInstalment.Sum() > 0m ? premiumPerInstalment : even;
        for (var c = 0; c < amounts.Count; c++)
        {
            result[c] ??= CentAllocation.Allocate(amounts[c], weights);
        }

        return result;
    }
}
