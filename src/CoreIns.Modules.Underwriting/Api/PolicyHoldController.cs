using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Underwriting.Api;

/// <summary>REST facade of policy holds and the accumulation check (BO-UW-09).</summary>
[ApiController]
[Route("api/uw/v1")]
#pragma warning disable CA1822 // MVC does not treat static methods as actions.
internal sealed class PolicyHoldController : ControllerBase
{
    /// <summary>uw.PolicyHold.check: which Active holds apply to a quote's job attributes, risk locations and dates.</summary>
    [HttpPost("policy-holds/check")]
    [Authorize(Policy = "uw.PolicyHold.check")]
    public async Task<IResult> CheckAsync([FromBody] PolicyHoldCheckRequest request, [FromServices] PolicyHoldQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.CheckAsync(request, cancellationToken).ConfigureAwait(false));

    /// <summary>uw.PolicyHold.create: a Draft hold.</summary>
    [HttpPost("policy-holds")]
    [Authorize(Policy = "uw.PolicyHold.create")]
    public async Task<IResult> CreateAsync(
        [FromBody] PolicyHoldCreateRequest request, [FromServices] ICommandHandler<CreatePolicyHold, PolicyHoldCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreatePolicyHold(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.PolicyHold.update: change a Draft hold.</summary>
    [HttpPatch("policy-holds/{id:guid}")]
    [Authorize(Policy = "uw.PolicyHold.update")]
    public Task<IResult> UpdateAsync(
        Guid id, [FromBody] PolicyHoldUpdateRequest request, [FromServices] ICommandHandler<UpdatePolicyHold, PolicyHoldUpdateResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new UpdatePolicyHold(id, request), cancellationToken);

    /// <summary>uw.PolicyHold.copy: a new Draft hold from an existing one.</summary>
    [HttpPost("policy-holds/copy")]
    [Authorize(Policy = "uw.PolicyHold.copy")]
    public Task<IResult> CopyAsync(
        [FromBody] PolicyHoldCopyRequest request, [FromServices] ICommandHandler<CopyPolicyHold, PolicyHoldCopyResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new CopyPolicyHold(request), cancellationToken);

    /// <summary>uw.PolicyHold.activate: the hold starts to apply.</summary>
    [HttpPost("policy-holds/activate")]
    [Authorize(Policy = "uw.PolicyHold.activate")]
    public Task<IResult> ActivateAsync(
        [FromBody] PolicyHoldActivateRequest request, [FromServices] ICommandHandler<ActivatePolicyHold, PolicyHoldActivateResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new ActivatePolicyHold(request), cancellationToken);

    /// <summary>uw.PolicyHold.release: the hold stops applying.</summary>
    [HttpPost("policy-holds/release")]
    [Authorize(Policy = "uw.PolicyHold.release")]
    public Task<IResult> ReleaseAsync(
        [FromBody] PolicyHoldReleaseRequest request, [FromServices] ICommandHandler<ReleasePolicyHold, PolicyHoldReleaseResponse> handler, CancellationToken cancellationToken) =>
        RunAsync(handler, new ReleasePolicyHold(request), cancellationToken);

    /// <summary>uw.PolicyHold.impact: in-force policies a hold touches.</summary>
    [HttpPost("policy-holds/impact")]
    [Authorize(Policy = "uw.PolicyHold.impact")]
    public async Task<IResult> ImpactAsync([FromBody] PolicyHoldImpactRequest request, [FromServices] PolicyHoldQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.ImpactAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.Accumulation.check: exposure in a zone against the capacity of a peril.</summary>
    [HttpPost("accumulation/check")]
    [Authorize(Policy = "uw.Accumulation.check")]
    public async Task<IResult> AccumulationAsync([FromBody] AccumulationCheckRequest request, [FromServices] PolicyHoldQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.AccumulationAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    private async Task<IResult> RunAsync<TCommand, TResult>(ICommandHandler<TCommand, TResult> handler, TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand<TResult>
    {
        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    private static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
