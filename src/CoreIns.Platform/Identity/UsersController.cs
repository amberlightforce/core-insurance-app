using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Platform.Identity;

/// <summary>
/// REST facade of the user directory (BO-PLT-05): <c>plt.User.*</c>, <c>plt.RoleAssignment.grant/revoke</c> and
/// <c>plt.UserProfile.getPreferences/setLanguage</c>. Entra ID stays the identity source (D-ARC-03); these are the in-app
/// directory entries and role records, administered by <c>Platform.Admin</c>.
/// </summary>
[ApiController]
[Route("api/plt/v1")]
internal sealed class UsersController : ControllerBase
{
    /// <summary>plt.User.create.</summary>
    [HttpPost("users")]
    [Authorize(Policy = "plt.User.create")]
    public async Task<IResult> CreateAsync([FromBody] UserCreateRequest body, [FromServices] ICommandHandler<CreateUser, UserCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateUser(body), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Created($"/api/plt/v1/users/{result.Value.User.UserId}", result.Value) : result.ToHttpResult(HttpContext);
    }

    /// <summary>plt.User.update.</summary>
    [HttpPatch("users/{id}")]
    [Authorize(Policy = "plt.User.update")]
    public async Task<IResult> UpdateAsync(
        string id, [FromBody] UserUpdateRequest body, [FromServices] ICommandHandler<UpdateUser, UserUpdateResponse> handler, CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var userId)
            ? (await handler.HandleAsync(new UpdateUser(userId, body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext)
            : NotFound<UserUpdateResponse>();

    /// <summary>plt.User.get.</summary>
    [HttpGet("users/{id}")]
    [Authorize(Policy = "plt.User.get")]
    public async Task<IResult> GetAsync(string id, [FromServices] UserDirectoryQueries queries, CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var userId)
            ? (await queries.GetAsync(userId, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext)
            : NotFound<UserGetResponse>();

    /// <summary>plt.User.search: the directory of the caller's legal entity.</summary>
    [HttpGet("users/search")]
    [Authorize(Policy = "plt.User.search")]
    public async Task<IResult> SearchAsync(
        [FromQuery] string? q, [FromQuery] UserStatus? status, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] UserDirectoryQueries queries,
        CancellationToken cancellationToken) =>
        (await queries.SearchAsync(q, status, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.User.suspend.</summary>
    [HttpPost("users/suspend")]
    [Authorize(Policy = "plt.User.suspend")]
    public async Task<IResult> SuspendAsync([FromBody] UserSuspendRequest body, [FromServices] ICommandHandler<SuspendUser, UserSuspendResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SuspendUser(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.User.reactivate.</summary>
    [HttpPost("users/reactivate")]
    [Authorize(Policy = "plt.User.reactivate")]
    public async Task<IResult> ReactivateAsync(
        [FromBody] UserReactivateRequest body, [FromServices] ICommandHandler<ReactivateUser, UserReactivateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReactivateUser(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.User.disable (final).</summary>
    [HttpPost("users/disable")]
    [Authorize(Policy = "plt.User.disable")]
    public async Task<IResult> DisableAsync([FromBody] UserDisableRequest body, [FromServices] ICommandHandler<DisableUser, UserDisableResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DisableUser(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.RoleAssignment.grant: requests the second approver, then records the approved assignment.</summary>
    [HttpPost("role-assignments/grant")]
    [Authorize(Policy = "plt.RoleAssignment.grant")]
    public async Task<IResult> GrantAsync([FromBody] RoleAssignmentGrantRequest body, [FromServices] ICommandHandler<GrantRole, RoleAssignmentGrantResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new GrantRole(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.RoleAssignment.revoke.</summary>
    [HttpPost("role-assignments/revoke")]
    [Authorize(Policy = "plt.RoleAssignment.revoke")]
    public async Task<IResult> RevokeAsync([FromBody] RoleAssignmentRevokeRequest body, [FromServices] ICommandHandler<RevokeRole, RoleAssignmentRevokeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RevokeRole(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.UserProfile.getPreferences: the signed-in user's preferences.</summary>
    [HttpGet("user-profile/get-preferences")]
    [Authorize(Policy = "plt.UserProfile.getPreferences")]
    public async Task<IResult> GetPreferencesAsync([FromServices] UserDirectoryQueries queries, CancellationToken cancellationToken) =>
        (await queries.GetPreferencesAsync(cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>plt.UserProfile.setLanguage: the signed-in user's own language.</summary>
    [HttpPost("user-profile/set-language")]
    [Authorize(Policy = "plt.UserProfile.setLanguage")]
    public async Task<IResult> SetLanguageAsync(
        [FromBody] UserProfileSetLanguageRequest body, [FromServices] ICommandHandler<SetUserLanguage, UserProfileSetLanguageResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SetUserLanguage(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private IResult NotFound<T>() => ((Result<T>)UserErrors.NotFound("user")).ToHttpResult(HttpContext);
}
