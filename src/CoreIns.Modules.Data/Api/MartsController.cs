using CoreIns.Modules.Data.Commands;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Modules.Data.Services.Marts;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Data.Api;

/// <summary>REST facade of the report marts: run, get, figure, compare (BO-DAT-03; contracts/openapi/dat.yaml).</summary>
[ApiController]
internal sealed class MartsController : ControllerBase
{
    /// <summary>dat.Mart.run (202: the run is computed inline and the run id is the receipt).</summary>
    [HttpPost("api/dat/v1/marts/run")]
    [Authorize(Policy = "dat.Mart.run")]
    public async Task<IResult> RunAsync(
        [FromBody] MartRunRequest request, [FromServices] ICommandHandler<RunMart, MartRunResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RunMart(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Json(result.Value, statusCode: StatusCodes.Status202Accepted) : result.ToHttpResult(HttpContext);
    }

    /// <summary>dat.Mart.adjust: adds an adjustment line (reason and evidence required); the stored figures never change.</summary>
    [HttpPost("api/dat/v1/marts/adjust")]
    [Authorize(Policy = "dat.Mart.adjust")]
    public async Task<IResult> AdjustAsync([FromBody] MartAdjustRequest request, [FromServices] ICommandHandler<AdjustMart, MartAdjustResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AdjustMart(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.Mart.signOff: a second person signs the run with its adjustments.</summary>
    [HttpPost("api/dat/v1/marts/sign-off")]
    [Authorize(Policy = "dat.Mart.signOff")]
    public async Task<IResult> SignOffAsync([FromBody] MartSignOffRequest request, [FromServices] ICommandHandler<SignOffMart, MartSignOffResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SignOffMart(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.Mart.export: the deterministic file of a signed-off run.</summary>
    [HttpPost("api/dat/v1/marts/export")]
    [Authorize(Policy = "dat.Mart.export")]
    public async Task<IResult> ExportAsync([FromBody] MartExportRequest request, [FromServices] ICommandHandler<ExportMart, MartExportResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ExportMart(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.Mart.list: the runs of the caller's legal entity, newest first (BO-DAT-05).</summary>
    [HttpGet("api/dat/v1/marts/runs")]
    [Authorize(Policy = "dat.Mart.list")]
    public async Task<IResult> ListAsync([FromQuery] string? mart, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] MartEngine engine, CancellationToken cancellationToken) =>
        Scope() is { } legalEntity ? (await engine.ListAsync(legalEntity, mart, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext) : NoScope();

    /// <summary>dat.Mart.get.</summary>
    [HttpGet("api/dat/v1/marts/{id}")]
    [Authorize(Policy = "dat.Mart.get")]
    public async Task<IResult> GetAsync(string id, [FromServices] MartEngine engine, CancellationToken cancellationToken) =>
        Scope() is { } legalEntity ? (await engine.GetAsync(legalEntity, id, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext) : NoScope();

    /// <summary>dat.Mart.figure.</summary>
    [HttpGet("api/dat/v1/marts/figure")]
    [Authorize(Policy = "dat.Mart.figure")]
    public async Task<IResult> FigureAsync([FromQuery] string? runId, [FromQuery] string? figureKey, [FromServices] MartEngine engine, CancellationToken cancellationToken) =>
        Scope() is { } legalEntity ? (await engine.FigureAsync(legalEntity, runId, figureKey, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext) : NoScope();

    /// <summary>dat.Mart.compare.</summary>
    [HttpGet("api/dat/v1/marts/compare")]
    [Authorize(Policy = "dat.Mart.compare")]
    public async Task<IResult> CompareAsync(
        [FromQuery] string? runA, [FromQuery] string? runB, [FromQuery] string? mart, [FromQuery] string? period, [FromQuery] string? productCode,
        [FromQuery] string? validAt, [FromQuery] string? knownAtA, [FromQuery] string? knownAtB, [FromServices] MartEngine engine, CancellationToken cancellationToken) =>
        Scope() is { } legalEntity
            ? (await engine.CompareAsync(legalEntity, runA, runB, mart, period, productCode, validAt, knownAtA, knownAtB, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext)
            : NoScope();

    private string? Scope() => HttpContext.RequestServices.GetRequiredService<RequestContext>().LegalEntity?.Value;

    private IResult NoScope() =>
        HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>()
            .ToResult(DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "No legal entity in scope."), HttpContext);
}
