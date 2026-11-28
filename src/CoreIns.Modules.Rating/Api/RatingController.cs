using Microsoft.Extensions.DependencyInjection;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Rating.Api;

/// <summary>Permissions of the rating operations (the contracts' <c>x-permission</c>).</summary>
internal static class RatingPermissions
{
    public const string Rate = "rat.Rate.rate";
    public const string WorksheetGet = "rat.Worksheet.get";
    public const string WorksheetExplain = "rat.Worksheet.explain";
    public const string WorksheetReperform = "rat.Worksheet.reperform";
    public const string WorksheetExport = "rat.Worksheet.export";
    public const string WorksheetAttach = "rat.Worksheet.attach";
    public const string ArtifactResolve = "rat.RatingArtifact.resolve";
}

/// <summary>REST facade of the rating operations. Controllers stay thin: bind the contract DTO, call the in-process service, map failures.</summary>
[ApiController]
[Route("api/rat/v1")]
internal sealed class RatingController : ControllerBase
{
    /// <summary>rat.Rate.rate: a pure rating sent as POST (contract x-operation-kind query, optional Idempotency-Key).</summary>
    [HttpPost("rates/rate")]
    [SkipIdempotency]
    [Authorize(Policy = RatingPermissions.Rate)]
    public async Task<IResult> RateAsync([FromBody] RateRateRequest request, [FromServices] IRatingRateService service, CancellationToken cancellationToken) =>
        await RunAsync(() => service.RateAsync(request, cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.Worksheet.explain: plain-language step text of a stored worksheet (BO-RAT-02).</summary>
    [HttpGet("worksheets/explain")]
    [Authorize(Policy = RatingPermissions.WorksheetExplain)]
    public async Task<IResult> ExplainWorksheetAsync(
        [FromQuery] string? worksheetId, [FromQuery] string? language, [FromServices] IRatingWorksheetService service, CancellationToken cancellationToken) =>
        await RunAsync(() => service.ExplainAsync(worksheetId ?? string.Empty, language, cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.Worksheet.reperform: re-rate under the pinned hashes and report drift (a query sent as POST).</summary>
    [HttpPost("worksheets/reperform")]
    [SkipIdempotency]
    [Authorize(Policy = RatingPermissions.WorksheetReperform)]
    public async Task<IResult> ReperformWorksheetAsync([FromBody] WorksheetReperformRequest request, [FromServices] RatingWorksheetService service, CancellationToken cancellationToken) =>
        await RunAsync(() => service.ReperformAsync(request, cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.Worksheet.export: CSV or JSON document of a stored worksheet (a query sent as POST).</summary>
    [HttpPost("worksheets/export")]
    [SkipIdempotency]
    [Authorize(Policy = RatingPermissions.WorksheetExport)]
    public async Task<IResult> ExportWorksheetAsync([FromBody] WorksheetExportRequest request, [FromServices] RatingWorksheetService service, CancellationToken cancellationToken) =>
        await RunAsync(() => service.ExportAsync(request, cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.Worksheet.attach: link worksheets to a policy transaction (idempotent on the Idempotency-Key).</summary>
    [HttpPost("worksheets/attach")]
    [Authorize(Policy = RatingPermissions.WorksheetAttach)]
    public async Task<IResult> AttachWorksheetsAsync(
        [FromBody] WorksheetAttachRequest request, [FromServices] IRatingWorksheetService service, CancellationToken cancellationToken) =>
        await RunAsync(() => service.AttachAsync(request, CommandOptions.New(), cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.Worksheet.get.</summary>
    [HttpGet("worksheets/{id}")]
    [Authorize(Policy = RatingPermissions.WorksheetGet)]
    public async Task<IResult> GetWorksheetAsync(string id, [FromServices] IRatingWorksheetService service, CancellationToken cancellationToken) =>
        await RunAsync(() => service.GetAsync(id, cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.RatingArtifact.resolve: active artefact for a product version on a date.</summary>
    [HttpPost("rating-artifacts/resolve")]
    [SkipIdempotency]
    [Authorize(Policy = RatingPermissions.ArtifactResolve)]
    public async Task<IResult> ResolveAsync(
        [FromBody] RatingArtifactResolveRequest request, [FromQuery] string? validAt, [FromServices] IRatingRatingArtifactService service, CancellationToken cancellationToken)
    {
        if (!BusinessDate.TryParse(validAt, out var date))
        {
            return Problem(DomainError.Of(ModuleCode.RAT, "ENVELOPE", "validAt must be a date."), HttpContext);
        }

        return await RunAsync(() => service.ResolveAsync(request, ValidAt.From(date), null, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<IResult> RunAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return Results.Ok(await call().ConfigureAwait(false));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Error, HttpContext);
        }
    }

    private static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
