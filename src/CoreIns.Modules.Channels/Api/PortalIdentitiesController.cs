using CoreIns.Modules.Channels.Commands;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Api;

/// <summary>REST facade of <c>chn.PortalIdentity.*</c> (BO-CHN-01): staff link and unlink a customer sign-in identity to its party.</summary>
[ApiController]
[Route("api/chn/v1/portal-identities")]
internal sealed class PortalIdentitiesController : ControllerBase
{
    /// <summary>chn.PortalIdentity.link.</summary>
    [HttpPost("link")]
    [Authorize(Policy = "chn.PortalIdentity.link")]
    public async Task<IResult> LinkAsync(
        [FromBody] PortalIdentityLinkRequest request,
        [FromServices] ICommandHandler<LinkPortalIdentity, PortalIdentityLinkResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new LinkPortalIdentity(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.PortalIdentity.unlink.</summary>
    [HttpPost("unlink")]
    [Authorize(Policy = "chn.PortalIdentity.unlink")]
    public async Task<IResult> UnlinkAsync(
        [FromBody] PortalIdentityUnlinkRequest request,
        [FromServices] ICommandHandler<UnlinkPortalIdentity, PortalIdentityLinkResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UnlinkPortalIdentity(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>chn.PortalIdentity.get: the live link of a subject.</summary>
    [HttpGet("{subject}")]
    [Authorize(Policy = "chn.PortalIdentity.get")]
    public async Task<IResult> GetAsync(
        string subject, [FromServices] ChannelsDbContext db, [FromServices] RequestContext context, [FromServices] ILegalEntityDirectory legalEntities,
        CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity!.Value);
        var row = await db.PortalIdentities.AsNoTracking()
            .SingleOrDefaultAsync(r => r.LegalEntityId == legalEntity && r.Subject == subject && r.Status == PortalIdentityStatuses.Linked, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(PortalIdentitySupport.NotFound(), HttpContext)
            : Results.Ok(new PortalIdentityLinkResponse { Identity = PortalIdentitySupport.ToView(row) });
    }
}
