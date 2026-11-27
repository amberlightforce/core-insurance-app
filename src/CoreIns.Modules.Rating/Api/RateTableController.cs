using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Services;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Rating.Api;

/// <summary>Permissions of the rate-table authoring operations (the contract's <c>x-permission</c>).</summary>
internal static class RateTablePermissions
{
    public const string Import = "rat.RateTable.import";
    public const string Validate = "rat.RateTable.validate";
    public const string Diff = "rat.RateTable.diff";
}

/// <summary>REST facade of rat.RateTable.import / validate / diff (BO-RAT-05). Writes only content-addressed rate_table_version rows.</summary>
[ApiController]
[Route("api/rat/v1")]
internal sealed class RateTableController : ControllerBase
{
    /// <summary>rat.RateTable.import: CSV/JSON to a rate table version; X-Dry-Run validates only.</summary>
    [HttpPost("rate-tables/import")]
    [Authorize(Policy = RateTablePermissions.Import)]
    public async Task<IResult> ImportAsync([FromBody] RateTableImportRequest request, [FromServices] RateTableImportService service, CancellationToken cancellationToken) =>
        await RunAsync(() => service.ImportAsync(request, cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.RateTable.validate: dry-run with every finding, plus gap and overlap detection.</summary>
    [HttpPost("rate-tables/validate")]
    [SkipIdempotency]
    [Authorize(Policy = RateTablePermissions.Validate)]
    public async Task<IResult> ValidateAsync([FromBody] RateTableValidateRequest request, [FromServices] RateTableImportService service, CancellationToken cancellationToken) =>
        await RunAsync(() => service.ValidateAsync(request, cancellationToken)).ConfigureAwait(false);

    /// <summary>rat.RateTable.diff: cell-level diff of two table versions (by table hash).</summary>
    [HttpGet("rate-tables/diff")]
    [Authorize(Policy = RateTablePermissions.Diff)]
    public async Task<IResult> DiffAsync([FromQuery] string? from, [FromQuery] string? to, [FromServices] RateTableImportService service, CancellationToken cancellationToken) =>
        await RunAsync(() => service.DiffAsync(from, to, cancellationToken)).ConfigureAwait(false);

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
