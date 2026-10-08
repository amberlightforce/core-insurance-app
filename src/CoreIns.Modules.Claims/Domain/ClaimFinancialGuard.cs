using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Modules.Claims.Domain;

/// <summary>Financial position of one exposure that the close guard needs (REQ-CLM-072).</summary>
/// <param name="ExposureId">Exposure.</param>
/// <param name="OpenReserve">Open (indemnity and expense) reserve, derived from the financial transactions (REQ-CLM-095); recovery reserves excluded.</param>
/// <param name="PaymentPending">True when a payment is pending, approved, on hold or awaiting disbursement (not yet Issued, Cleared or Voided).</param>
internal sealed record ExposureFinancialPosition(ExposureId ExposureId, decimal OpenReserve, bool PaymentPending);

/// <summary>
/// The seam the close guard (REQ-CLM-072/073) calls: "open reserve is zero and no payment pending", read inside the
/// caller's transaction (the claim row is locked by the closing command, and every financial command locks it too).
/// </summary>
internal interface IClaimFinancialGuard
{
    /// <summary>The position of each exposure of the claim (one entry per id asked).</summary>
    Task<IReadOnlyList<ExposureFinancialPosition>> PositionsAsync(ClaimId claimId, IReadOnlyList<ExposureId> exposures, CancellationToken cancellationToken);
}

/// <summary>
/// The close guard over the derived balances (SL2-CLM-MONEY): open reserve per exposure = Σ over its lines of
/// (Σ approved reserves − Σ approved eroding payments, never below zero); a payment is pending while its set awaits a
/// decision or it is approved, on hold or submitted to BIL (not yet issued).
/// </summary>
internal sealed class DerivedClaimFinancials(Queries.FinancialsReader reader) : IClaimFinancialGuard
{
    public async Task<IReadOnlyList<ExposureFinancialPosition>> PositionsAsync(ClaimId claimId, IReadOnlyList<ExposureId> exposures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exposures);
        var lines = await reader.LinesAsync(claimId, cancellationToken).ConfigureAwait(false);
        var amounts = await reader.ApprovedAsync(claimId, null, cancellationToken).ConfigureAwait(false);
        var pending = await reader.PaymentPendingAsync(claimId, cancellationToken).ConfigureAwait(false);
        return [.. exposures.Select(e => new ExposureFinancialPosition(
            e,
            lines.Where(l => l.ExposureId == e).Sum(l => amounts.GetValueOrDefault(l.ReserveLineId).OpenReserve),
            pending.Contains(e)))];
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
