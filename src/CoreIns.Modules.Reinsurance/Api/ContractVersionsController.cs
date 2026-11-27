using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>
/// REST facade of the contract versions (BO-RI-03, contracts/openapi/ri.yaml): endorse, decide an endorsement, and the
/// bitemporal <c>version-at</c> read. Thin; commands go through the pipeline.
/// </summary>
[ApiController]
[Route("api/ri/v1/contracts")]
internal sealed class ContractVersionsController : ControllerBase
{
    /// <summary>ri.Contract.endorse: a new pending version from an effective date, with a PLT approval.</summary>
    [HttpPost("endorse")]
    [Authorize(Policy = ReinsurancePermissions.ContractEndorse)]
    public async Task<IResult> EndorseAsync(
        [FromBody] ContractEndorseRequest request, [FromServices] ICommandHandler<EndorseContract, ContractEndorseResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new EndorseContract(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Contract.approveEndorsement: the checker's APPROVE or REJECT of a pending endorsement.</summary>
    [HttpPost("approve-endorsement")]
    [Authorize(Policy = ReinsurancePermissions.ContractApproveEndorsement)]
    public async Task<IResult> ApproveEndorsementAsync(
        [FromBody] ContractApproveEndorsementRequest request, [FromServices] ICommandHandler<ApproveEndorsement, ContractApproveEndorsementResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveEndorsement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Contract.versionAt: the version in force at <c>validAt</c> as known at <c>knownAt</c> (both default to now).</summary>
    [HttpGet("version-at")]
    [Authorize(Policy = ReinsurancePermissions.ContractVersionAt)]
    public async Task<IResult> VersionAtAsync(
        [FromQuery] string? contractId, [FromQuery] string? validAt, [FromQuery] string? knownAt, [FromServices] ContractVersionQuery query, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(contractId, out var id) || id == Guid.Empty)
        {
            return RiHttp.Problem(RiErrors.Validation("contractId", "CONTRACT_ID_REQUIRED", "contractId must be a contract id."), HttpContext);
        }

        ValidAt? valid = null;
        if (!string.IsNullOrWhiteSpace(validAt))
        {
            if (Instant.TryParse(validAt, out var instant))
            {
                valid = ValidAt.From(instant);
            }
            else if (BusinessDate.TryParse(validAt, out var date))
            {
                valid = ValidAt.From(date);
            }
            else
            {
                return RiHttp.Problem(RiErrors.Validation("validAt", "VALID_AT", "validAt must be a date or an instant."), HttpContext);
            }
        }

        Instant? known = null;
        if (!string.IsNullOrWhiteSpace(knownAt))
        {
            if (!Instant.TryParse(knownAt, out var parsed))
            {
                return RiHttp.Problem(RiErrors.Validation("knownAt", "KNOWN_AT", "knownAt must be an instant."), HttpContext);
            }

            known = parsed;
        }

        return (await query.VersionAtAsync(new RiContractId(id), valid, known, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }
}
