using CoreIns.Modules.Policy.Commands.Renewal;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>Permission names of the renewal operations (the operations' <c>x-permission</c>).</summary>
internal static class RenewalPermissions
{
    public const string Create = "pol.Renewal.create";
    public const string Offer = "pol.Renewal.offer";
    public const string Accept = "pol.Renewal.accept";
}

/// <summary>REST facade of <c>pol.Renewal.*</c> (contracts/openapi/pol.yaml): thin, commands through the pipeline.</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class RenewalController : ControllerBase
{
    /// <summary>pol.Renewal.create ("Renew now") → 201, Location = the renewal job.</summary>
    [HttpPost("renewals")]
    [Authorize(Policy = RenewalPermissions.Create)]
    public async Task<IResult> CreateAsync(
        [FromBody] RenewalCreateRequest request, [FromServices] ICommandHandler<CreateRenewal, RenewalCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateRenewal(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pol/v1/jobs/{result.Value.JobId.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>pol.Renewal.offer.</summary>
    [HttpPost("renewals/offer")]
    [Authorize(Policy = RenewalPermissions.Offer)]
    public async Task<IResult> OfferAsync(
        [FromBody] RenewalOfferRequest request, [FromServices] ICommandHandler<OfferRenewal, RenewalOfferResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new OfferRenewal(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Renewal.accept (explicit acceptance, then bind of the next term).</summary>
    [HttpPost("renewals/accept")]
    [Authorize(Policy = RenewalPermissions.Accept)]
    public async Task<IResult> AcceptAsync(
        [FromBody] RenewalAcceptRequest request, [FromServices] ICommandHandler<AcceptRenewal, RenewalAcceptResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AcceptRenewal(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
