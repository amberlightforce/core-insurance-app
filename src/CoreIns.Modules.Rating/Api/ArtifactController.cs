using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Rating.Api;

/// <summary>Permissions of the artefact catalogue reads (the contract's <c>x-permission</c>).</summary>
internal static class ArtifactPermissions
{
    public const string Get = "rat.RatingArtifact.get";
    public const string List = "rat.RatingArtifact.list";
    public const string Compatibility = "rat.RatingArtifact.compatibility";
}

/// <summary>REST facade of rat.RatingArtifact.get / list / compatibility (BO-RAT-04). Read-only.</summary>
[ApiController]
[Route("api/rat/v1")]
internal sealed class ArtifactController : ControllerBase
{
    /// <summary>rat.RatingArtifact.list: keyset-paged catalogue with lifecycle status and data status.</summary>
    [HttpGet("rating-artifacts")]
    [Authorize(Policy = ArtifactPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? productCode, [FromQuery] string? status, [FromQuery] string? dataStatus,
        [FromServices] ArtifactQueries queries, CancellationToken cancellationToken) =>
        await RunAsync(() => queries.ListAsync(cursor, limit, productCode, status, dataStatus, cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.RatingArtifact.compatibility: artefact against a product version.</summary>
    [HttpGet("rating-artifacts/compatibility")]
    [Authorize(Policy = ArtifactPermissions.Compatibility)]
    public async Task<IResult> CompatibilityAsync(
        [FromQuery] string? artefactHash, [FromQuery] string? productVersion, [FromServices] ArtifactQueries queries, CancellationToken cancellationToken) =>
        await RunAsync(() => queries.CompatibilityAsync(artefactHash, productVersion, cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.RatingArtifact.get: manifest, status and compatibility of one artefact by hash.</summary>
    [HttpGet("rating-artifacts/{id}")]
    [Authorize(Policy = ArtifactPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] ArtifactQueries queries, CancellationToken cancellationToken) =>
        await RunAsync(() => queries.GetAsync(id, cancellationToken)).ConfigureAwait(false);

    private async Task<IResult> RunAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return Results.Ok(await call().ConfigureAwait(false));
        }
        catch (DomainException ex)
        {
            return HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(ex.Error, HttpContext);
        }
    }
}
