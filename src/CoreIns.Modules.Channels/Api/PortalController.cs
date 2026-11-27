using CoreIns.Modules.Channels.Queries;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Api;

/// <summary>
/// REST facade of <c>chn.Portal.*</c> (BO-CHN-02): the customer's read views. Every action resolves the caller through the CHN scope
/// service; no live portal link (staff, strangers, unlinked subjects) and not-owned policies all answer 404 so existence never leaks.
/// </summary>
[ApiController]
[Route("api/chn/v1/portal")]
internal sealed class PortalController(PortalReader reader) : ControllerBase
{
    /// <summary>chn.Portal.me.</summary>
    [HttpGet("me")]
    [Authorize(Policy = "chn.Portal.me")]
    public async Task<IResult> MeAsync(CancellationToken cancellationToken) => Respond(await reader.MeAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>chn.Portal.dashboard.</summary>
    [HttpGet("dashboard")]
    [Authorize(Policy = "chn.Portal.dashboard")]
    public async Task<IResult> DashboardAsync(CancellationToken cancellationToken) => Respond(await reader.DashboardAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>chn.Portal.policies.</summary>
    [HttpGet("policies")]
    [Authorize(Policy = "chn.Portal.policies")]
    public async Task<IResult> PoliciesAsync([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        Respond(await reader.PoliciesAsync(cursor, limit, cancellationToken).ConfigureAwait(false));

    /// <summary>chn.Portal.policy.</summary>
    [HttpGet("policies/{id}")]
    [Authorize(Policy = "chn.Portal.policy")]
    public async Task<IResult> PolicyAsync(string id, CancellationToken cancellationToken) => Respond(await reader.PolicyAsync(id, cancellationToken).ConfigureAwait(false));

    private IResult Respond<T>(T? body) where T : class =>
        body is null
            ? HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "Not found."), HttpContext)
            : Results.Ok(body);
}
