using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Domain;

/// <summary>Financial position of one exposure that the close guard needs (REQ-CLM-072).</summary>
/// <param name="ExposureId">Exposure.</param>
/// <param name="OpenReserve">Open (indemnity and expense) reserve, derived from the financial transactions (REQ-CLM-095); recovery reserves excluded.</param>
/// <param name="PaymentPending">True when a payment is pending, approved, on hold or awaiting disbursement (not yet Issued, Cleared or Voided).</param>
internal sealed record ExposureFinancialPosition(ExposureId ExposureId, decimal OpenReserve, bool PaymentPending);

/// <summary>
/// The seam the close guard (REQ-CLM-072/073) calls: "open reserve is zero and no payment pending". SL2-CLM-CORE has no
/// financials, so <see cref="NoClaimFinancials"/> answers zero / none; the claim financials work package (SL2-CLM-MONEY)
/// replaces the registration with its derived balances, read inside the caller's transaction.
/// </summary>
internal interface IClaimFinancialGuard
{
    /// <summary>The position of each exposure of the claim (one entry per id asked).</summary>
    Task<IReadOnlyList<ExposureFinancialPosition>> PositionsAsync(ClaimId claimId, IReadOnlyList<ExposureId> exposures, CancellationToken cancellationToken);
}

/// <summary>Until claim financials exist: no reserve, no payment (SL2-CLM-CORE).</summary>
internal sealed class NoClaimFinancials : IClaimFinancialGuard
{
    public Task<IReadOnlyList<ExposureFinancialPosition>> PositionsAsync(ClaimId claimId, IReadOnlyList<ExposureId> exposures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exposures);
        return Task.FromResult<IReadOnlyList<ExposureFinancialPosition>>([.. exposures.Select(e => new ExposureFinancialPosition(e, 0m, false))]);
    }
}

/// <summary>The close guard rule itself (pure; REQ-CLM-072): an exposure may close only with zero open reserve and no payment pending.</summary>
internal static class CloseGuard
{
    /// <summary>The exposures that block closing, with the reason codes OPEN_RESERVE and PAYMENT_PENDING.</summary>
    public static IReadOnlyList<(ExposureId Exposure, IReadOnlyList<string> Reasons)> Blocking(IEnumerable<ExposureFinancialPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var blocking = new List<(ExposureId, IReadOnlyList<string>)>();
        foreach (var position in positions)
        {
            var reasons = new List<string>();
            if (position.OpenReserve != 0m)
            {
                reasons.Add("OPEN_RESERVE");
            }

            if (position.PaymentPending)
            {
                reasons.Add("PAYMENT_PENDING");
            }

            if (reasons.Count > 0)
            {
                blocking.Add((position.ExposureId, reasons));
            }
        }

        return blocking;
    }
}
