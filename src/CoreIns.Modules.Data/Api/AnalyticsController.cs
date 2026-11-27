using CoreIns.Modules.Data.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Data.Api;

/// <summary>REST facade of the bitemporal conformed facts: as-of query, population aggregate and KPIs (BO-DAT-02; contracts/openapi/dat.yaml).</summary>
[ApiController]
internal sealed class AnalyticsController : ControllerBase
{
    /// <summary>dat.Query.asOf.</summary>
    [HttpGet("api/dat/v1/queries/as-of")]
    [Authorize(Policy = "dat.Query.asOf")]
    public async Task<IResult> AsOfAsync(
        [FromQuery] string? view, [FromQuery] string? validAt, [FromQuery] string? knownAt, [FromQuery] string? filters,
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] AnalyticsReader reader, CancellationToken cancellationToken)
    {
        if (Scope() is not { } legalEntity)
        {
            return NoScope();
        }

        return (await reader.AsOfAsync(legalEntity, view, validAt, knownAt, filters, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>dat.Population.aggregate.</summary>
    [HttpGet("api/dat/v1/populations/aggregate")]
    [Authorize(Policy = "dat.Population.aggregate")]
    public async Task<IResult> AggregateAsync(
        [FromQuery] string? productSlot, [FromQuery(Name = "as")] string? asValid, [FromQuery] string? validAt, [FromQuery] string? knownAt, [FromQuery] string? window,
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] AnalyticsReader reader, CancellationToken cancellationToken)
    {
        if (Scope() is not { } legalEntity)
        {
            return NoScope();
        }

        return (await reader.AggregateAsync(legalEntity, productSlot, asValid, validAt, knownAt, window, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    /// <summary>dat.Kpi.get.</summary>
    [HttpGet("api/dat/v1/kpis/get")]
    [Authorize(Policy = "dat.Kpi.get")]
    public async Task<IResult> KpiAsync(
        [FromQuery] string? kpiCode, [FromQuery] string? filters, [FromQuery] string? period, [FromQuery] string? validAt, [FromQuery] string? knownAt,
        [FromServices] AnalyticsReader reader, CancellationToken cancellationToken)
    {
        if (Scope() is not { } legalEntity)
        {
            return NoScope();
        }

        return (await reader.KpiAsync(legalEntity, kpiCode, filters, period, validAt, knownAt, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
    }

    private string? Scope() => HttpContext.RequestServices.GetRequiredService<RequestContext>().LegalEntity?.Value;

    private IResult NoScope() =>
        HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>()
            .ToResult(DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "No legal entity in scope."), HttpContext);
}
