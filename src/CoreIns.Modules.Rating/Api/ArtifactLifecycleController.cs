using CoreIns.Modules.Rating.Commands;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Rating.Api;

/// <summary>Permissions of the artefact lifecycle operations (the contract's <c>x-permission</c>).</summary>
internal static class LifecyclePermissions
{
    public const string CreateChangeSet = "rat.RatingChangeSet.create";
    public const string Build = "rat.RatingArtifact.build";
    public const string Submit = "rat.RatingArtifact.submit";
    public const string Return = "rat.RatingArtifact.return";
    public const string Schedule = "rat.RatingArtifact.schedule";
    public const string CancelSchedule = "rat.RatingArtifact.cancelSchedule";
    public const string Rollback = "rat.RatingArtifact.rollback";
}

/// <summary>REST facade of the rating artefact lifecycle (BO-RAT-06): change set, build, submit, return, schedule, cancel-schedule, rollback.</summary>
[ApiController]
[Route("api/rat/v1")]
internal sealed class ArtifactLifecycleController : ControllerBase
{
    /// <summary>rat.RatingChangeSet.create: a draft change set pinning rate-table hashes for one product version.</summary>
    [HttpPost("rating-change-sets")]
    [Authorize(Policy = LifecyclePermissions.CreateChangeSet)]
    public async Task<IResult> CreateChangeSetAsync(
        [FromBody] RatingChangeSetCreateRequest request,
        [FromServices] ICommandHandler<CreateRatingChangeSet, RatingChangeSetCreateResponse> handler, CancellationToken cancellationToken) =>
        Respond(await handler.HandleAsync(new CreateRatingChangeSet(request), cancellationToken).ConfigureAwait(false));

    /// <summary>rat.RatingArtifact.build: compile the change set into an immutable content-hashed artefact (dry run validates).</summary>
    [HttpPost("rating-artifacts/build")]
    [Authorize(Policy = LifecyclePermissions.Build)]
    public async Task<IResult> BuildAsync(
        [FromBody] RatingArtifactBuildRequest request,
        [FromServices] ICommandHandler<BuildRatingArtifact, RatingArtifactBuildResponse> handler, CancellationToken cancellationToken) =>
        Respond(await handler.HandleAsync(new BuildRatingArtifact(request), cancellationToken).ConfigureAwait(false));

    /// <summary>rat.RatingArtifact.submit: open the PLT approval (type RAT.Release).</summary>
    [HttpPost("rating-artifacts/submit")]
    [Authorize(Policy = LifecyclePermissions.Submit)]
    public async Task<IResult> SubmitAsync(
        [FromBody] RatingArtifactSubmitRequest request,
        [FromServices] ICommandHandler<SubmitRatingArtifact, RatingArtifactSubmitResponse> handler, CancellationToken cancellationToken) =>
        Respond(await handler.HandleAsync(new SubmitRatingArtifact(request), cancellationToken).ConfigureAwait(false));

    /// <summary>rat.RatingArtifact.return: the checker returns a submitted artefact to its maker.</summary>
    [HttpPost("rating-artifacts/return")]
    [Authorize(Policy = LifecyclePermissions.Return)]
    public async Task<IResult> ReturnAsync(
        [FromBody] RatingArtifactReturnRequest request,
        [FromServices] ICommandHandler<ReturnRatingArtifact, RatingArtifactReturnResponse> handler, CancellationToken cancellationToken) =>
        Respond(await handler.HandleAsync(new ReturnRatingArtifact(request), cancellationToken).ConfigureAwait(false));

    /// <summary>rat.RatingArtifact.schedule: the checker approves and sets the activation date (today activates at once).</summary>
    [HttpPost("rating-artifacts/schedule")]
    [Authorize(Policy = LifecyclePermissions.Schedule)]
    public async Task<IResult> ScheduleAsync(
        [FromBody] RatingArtifactScheduleRequest request,
        [FromServices] ICommandHandler<ScheduleRatingArtifact, RatingArtifactScheduleResponse> handler, CancellationToken cancellationToken) =>
        Respond(await handler.HandleAsync(new ScheduleRatingArtifact(request), cancellationToken).ConfigureAwait(false));

    /// <summary>rat.RatingArtifact.cancelSchedule: back out an activation whose date is still ahead.</summary>
    [HttpPost("rating-artifacts/cancel-schedule")]
    [Authorize(Policy = LifecyclePermissions.CancelSchedule)]
    public async Task<IResult> CancelScheduleAsync(
        [FromBody] RatingArtifactCancelScheduleRequest request,
        [FromServices] ICommandHandler<CancelRatingSchedule, RatingArtifactCancelScheduleResponse> handler, CancellationToken cancellationToken) =>
        Respond(await handler.HandleAsync(new CancelRatingSchedule(request), cancellationToken).ConfigureAwait(false));

    /// <summary>rat.RatingArtifact.rollback: withdraw the artefact in force and re-activate the one before it.</summary>
    [HttpPost("rating-artifacts/rollback")]
    [Authorize(Policy = LifecyclePermissions.Rollback)]
    public async Task<IResult> RollbackAsync(
        [FromBody] RatingArtifactRollbackRequest request,
        [FromServices] ICommandHandler<RollbackRatingArtifact, RatingArtifactRollbackResponse> handler, CancellationToken cancellationToken) =>
        Respond(await handler.HandleAsync(new RollbackRatingArtifact(request), cancellationToken).ConfigureAwait(false));

    private IResult Respond<T>(Result<T> result) =>
        result.IsSuccess
            ? Results.Ok(result.Value)
            : HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(result.Error!, HttpContext);
}
