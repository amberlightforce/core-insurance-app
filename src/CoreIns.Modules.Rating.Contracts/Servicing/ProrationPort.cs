using CoreIns.Modules.Rating.Contracts.Api;

namespace CoreIns.Modules.Rating.Contracts.Servicing;

/// <summary>
/// <c>rat.Proration.prorate</c> as an in-process service (REQ-RAT-004), over the generated request and response. The contract exposes
/// no generated in-process interface for this operation (exposure: ui), so this port is hand-written. Pure apart from the MKT rounding
/// call and the read of the product artefact's day count. Errors: <c>RAT-ERR-CONVENTION</c>, <c>RAT-ERR-PERIOD</c>, <c>RAT-ERR-INPUT</c>.
/// </summary>
public interface IRatingProrationEngine
{
    /// <summary>Turns annual rates into amounts for the given periods (half-open, whole Athens dates).</summary>
    Task<ProrationProrateResponse> ProrateAsync(ProrationProrateRequest request, CancellationToken cancellationToken = default);
}
