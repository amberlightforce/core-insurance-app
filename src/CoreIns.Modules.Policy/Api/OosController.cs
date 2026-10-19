using CoreIns.Modules.Policy.Commands.Change;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>
/// REST facade of <c>pol.Change.createOutOfSequence</c> (SL6-POL-OOS, contracts/openapi/pol.yaml): starts a change whose effective time may be
/// earlier than the term's latest bound transaction. The job is then edited, quoted and bound with the ordinary job operations; the bind
/// reverses and re-applies the later segments in one transaction (NET mode, D-SL6-10).
/// </summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class OosController : ControllerBase
{
    [HttpPost("policy-changes/out-of-sequence")]
    [Authorize(Policy = "pol.Change.createOutOfSequence")]
    public async Task<IResult> CreateAsync(
        [FromBody] ChangeCreateOutOfSequenceRequest request, [FromServices] ICommandHandler<CreatePolicyChange, PolicyChangeCreateResponse> handler, CancellationToken cancellationToken)
    {
        if (request.Mode is { } mode && mode != ChangeCreateOutOfSequenceRequest.ModeValue.Net)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.POL, "VALIDATION", "Only mode NET is enabled for an out-of-sequence change in P1-P2 (D-SL6-10)."), HttpContext);
        }

        var result = await handler.HandleAsync(
            new CreatePolicyChange(new PolicyChangeCreateRequest { PolicyId = request.PolicyId, EffectiveAt = request.EffectiveAt, Description = request.Description }, AllowOutOfSequence: true),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pol/v1/jobs/{result.Value.JobId.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }
}
