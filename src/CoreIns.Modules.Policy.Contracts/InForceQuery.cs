namespace CoreIns.Modules.Policy.Contracts;

/// <summary>
/// What the in-force count is asked for (BO-UW-09): every given dimension must match, within one dimension any value matches. An empty list
/// leaves its dimension open. A postcode ending in <c>*</c> is a prefix.
/// </summary>
/// <param name="ProductCodes">Product codes of the policies.</param>
/// <param name="Postcodes">Garaging postcodes of the risk (<c>vehicles[].fields.garagingPostcode</c>); a trailing <c>*</c> makes it a prefix.</param>
public sealed record InForceCriteria(IReadOnlyList<string> ProductCodes, IReadOnlyList<string> Postcodes);

/// <summary>The in-force policies that match <see cref="InForceCriteria"/> now.</summary>
/// <param name="Policies">Number of distinct policies.</param>
/// <param name="ByProduct">The same count per product code.</param>
public sealed record InForceCount(int Policies, IReadOnlyDictionary<string, int> ByProduct);

/// <summary>
/// The capability "in-force policies by location and product" that UW asks of POL (policy-hold impact, accumulation). In-process only (D-ARC-16);
/// the legal entity is the caller's.
/// </summary>
public interface IPolicyInForceQuery
{
    /// <summary>Counts the policies whose term is in force now and whose current risk matches <paramref name="criteria"/>.</summary>
    Task<InForceCount> CountAsync(InForceCriteria criteria, CancellationToken cancellationToken = default);
}
