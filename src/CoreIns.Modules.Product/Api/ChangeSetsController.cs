using CoreIns.Modules.Product.Commands;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Platform.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Product.Api;

/// <summary>REST facade of the change-set (draft) store, BO-PFC-01: create, edit, abandon. Revert and rebase: BO-PFC-02.</summary>
[ApiController]
[Route("api/pfc/v1/change-sets")]
internal sealed class ChangeSetsController : ControllerBase
{
    /// <summary>pfc.ChangeSet.create → 201: a draft copied from a Locked version.</summary>
    [HttpPost]
    [Authorize(Policy = ProductPermissions.ChangeSetCreate)]
    public async Task<IResult> CreateAsync(
        [FromBody] ChangeSetCreateRequest request,
        [FromServices] ICommandHandler<CreateChangeSet, ChangeSetCreateResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateChangeSet(request), cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? this.ProblemOf(result.Error)
            : Results.Created($"/api/pfc/v1/change-sets/{result.Value.ChangeSetState.ChangeSetId:D}", result.Value);
    }

    /// <summary>pfc.ChangeSet.edit: JSON-pointer operations on the draft; lint findings are returned, the draft is saved anyway.</summary>
    [HttpPost("edit")]
    [Authorize(Policy = ProductPermissions.ChangeSetEdit)]
    public async Task<IResult> EditAsync(
        [FromBody] ChangeSetEditRequest request,
        [FromServices] ICommandHandler<EditChangeSet, ChangeSetEditResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new EditChangeSet(request), cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.ChangeSet.abandon: closes an open change set.</summary>
    [HttpPost("abandon")]
    [Authorize(Policy = ProductPermissions.ChangeSetAbandon)]
    public async Task<IResult> AbandonAsync(
        [FromBody] ChangeSetAbandonRequest request,
        [FromServices] ICommandHandler<AbandonChangeSet, ChangeSetAbandonResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new AbandonChangeSet(request), cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.ChangeSet.revert: undoes one logged edit.</summary>
    [HttpPost("revert")]
    [Authorize(Policy = ProductPermissions.ChangeSetRevert)]
    public async Task<IResult> RevertAsync(
        [FromBody] ChangeSetRevertRequest request,
        [FromServices] ICommandHandler<RevertChangeSet, ChangeSetRevertResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new RevertChangeSet(request), cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.ChangeSet.rebase: three-way merge of the draft onto a newer Locked version; conflicts give 409 with the paths.</summary>
    [HttpPost("rebase")]
    [Authorize(Policy = ProductPermissions.ChangeSetRebase)]
    public async Task<IResult> RebaseAsync(
        [FromBody] ChangeSetRebaseRequest request,
        [FromServices] ICommandHandler<RebaseChangeSet, ChangeSetRebaseResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new RebaseChangeSet(request), cancellationToken).ConfigureAwait(false));
}
