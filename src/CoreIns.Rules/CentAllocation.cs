namespace CoreIns.Rules;

/// <summary>
/// THE largest-remainder cent allocator (NX-EC-06). Every place that splits one money total over several parts goes through here,
/// so the parts always sum exactly to the total and no part deviates from its exact share by more than one cent.
/// </summary>
public static class CentAllocation
{
    /// <summary>
    /// Splits <paramref name="total"/> over <paramref name="weights"/> proportionally (weights must be non-negative and sum above zero).
    /// Works for negative totals. Ties go to the lower index, so callers order their parts (e.g. lead first, then id).
    /// </summary>
    public static decimal[] Allocate(decimal total, IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0 || weights.Any(w => w < 0m))
        {
            throw new ArgumentException("Weights must be a non-empty list of non-negative values.", nameof(weights));
        }

        var sum = weights.Sum();
        if (sum <= 0m)
        {
            throw new ArgumentException("Weights must sum to more than zero.", nameof(weights));
        }

        return Distribute(total, [.. weights.Select(w => total * w / sum)]);
    }

    /// <summary>
    /// Rounds each exact amount to cents such that the parts sum to <paramref name="total"/>: floor to cents (toward negative
    /// infinity, so remainders are never negative), then give the remaining cents to the largest remainders (ties: lower index).
    /// A sub-cent residual (a total finer than cents) goes to the first part so the sum stays exact.
    /// </summary>
    public static decimal[] Distribute(decimal total, IReadOnlyList<decimal> exacts)
    {
        ArgumentNullException.ThrowIfNull(exacts);
        if (exacts.Count == 0)
        {
            throw new ArgumentException("At least one part is required.", nameof(exacts));
        }

        var floors = exacts.Select(e => Math.Floor(e * 100m) / 100m).ToArray();
        var remainders = exacts.Select((e, i) => e - floors[i]).ToArray();
        var cents = (decimal)Math.Truncate((total - floors.Sum()) * 100m);
        var order = Enumerable.Range(0, floors.Length).OrderByDescending(i => remainders[i]).ThenBy(i => i).ToArray();
        for (var k = 0; k < order.Length && k < cents; k++)
        {
            floors[order[k]] += 0.01m;
        }

        floors[0] += total - floors.Sum();
        return floors;
    }
}
