using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Platform.Approvals;

/// <summary>Permission names of the maker-checker operations (<c>x-permission</c>), granted in <c>Platform:Permissions</c>.</summary>
internal static class ApprovalPermissions
{
    public const string Decide = "plt.Approval.decide";
    public const string Get = "plt.Approval.get";
    public const string List = "plt.Approval.list";
}

/// <summary>
/// REST facade of <c>plt.Approval.*</c> (contracts/openapi/plt.yaml): decide, get and the approvals inbox (<c>list</c>).
/// <c>plt.Approval.request</c> is in-process only (SL2-PLT review D1): the owning module fixes the subject, the required
/// authority, the referral role and supersession, never a maker over HTTP. The checker is the signed-in actor.
/// </summary>
[ApiController]
[Route("api/plt/v1/approval")]
internal sealed class ApprovalsController : ControllerBase
{
    /// <summary>plt.Approval.decide: approve or reject the content hash the checker reviewed.</summary>
    [HttpPost("decide")]
    [Authorize(Policy = ApprovalPermissions.Decide)]
    public async Task<IResult> DecideAsync(
        [FromBody] ApprovalDecideRequest body, [FromServices] ICommandHandler<DecideApproval, ApprovalDecideResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DecideApproval(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.Approval.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = ApprovalPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] ApprovalQueries queries, CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var requestId)
            ? (await queries.GetAsync(requestId, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext)
            : ((Result<ApprovalGetResponse>)DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "The approval request does not exist.")).ToHttpResult(HttpContext);

    /// <summary>plt.Approval.list: the inbox of the caller's roles (default status PendingApproval).</summary>
    [HttpGet]
    [Authorize(Policy = ApprovalPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] ApprovalStatus? status, [FromQuery] string? role, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] ApprovalQueries queries,
        CancellationToken cancellationToken) =>
        (await queries.ListAsync(status, role, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
