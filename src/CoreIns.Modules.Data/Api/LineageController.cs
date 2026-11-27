using CoreIns.Modules.Data.Services.Coverage;
using CoreIns.Modules.Data.Services.Lineage;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Data.Api;

/// <summary>Lineage of mart figures and coverage control totals (BO-DAT-05; contracts/openapi/dat.yaml). Both are read-only.</summary>
[ApiController]
internal sealed class LineageController : ControllerBase
{
    /// <summary>dat.Lineage.trace: figure -> run -> source rows -> raw event ids.</summary>
    [HttpGet("api/dat/v1/lineage/trace")]
    [Authorize(Policy = "dat.Lineage.trace")]
    public async Task<IResult> TraceAsync(
        [FromQuery] string? node, [FromQuery] string? runId, [FromQuery] string? figureKey, [FromQuery] string? direction, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] LineageTracer tracer, CancellationToken cancellationToken) =>
        Scope() is { } legalEntity
            ? (await tracer.TraceAsync(legalEntity, node, runId, figureKey, direction, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext)
            : NoScope();

    /// <summary>dat.Coverage.status: projected / quarantined / gap / not-handled counts per fact table and mart, per period.</summary>
    [HttpGet("api/dat/v1/coverage/status")]
    [Authorize(Policy = "dat.Coverage.status")]
    public async Task<IResult> CoverageAsync([FromQuery] string? period, [FromServices] CoverageReader reader, CancellationToken cancellationToken) =>
        Scope() is { } legalEntity ? (await reader.StatusAsync(legalEntity, period, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext) : NoScope();

    private string? Scope() => HttpContext.RequestServices.GetRequiredService<RequestContext>().LegalEntity?.Value;

    private IResult NoScope() =>
        HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>()
            .ToResult(DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "No legal entity in scope."), HttpContext);
}
