using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Compliance.Api;

/// <summary>Permission names of the clock operations (<c>x-permission</c>).</summary>
internal static class ClockPermissions
{
    public const string Start = "cmp.Clock.start";
    public const string Pause = "cmp.Clock.pause";
    public const string Resume = "cmp.Clock.resume";
    public const string Stop = "cmp.Clock.stop";
    public const string Cancel = "cmp.Clock.cancel";
    public const string Extend = "cmp.Clock.extend";
    public const string Get = "cmp.Clock.get";
    public const string Query = "cmp.Clock.query";
    public const string DefList = "cmp.ClockDef.list";
    public const string DefGet = "cmp.ClockDef.get";
}

/// <summary>REST facade of <c>cmp.Clock.*</c> and <c>cmp.ClockDef.*</c> (contracts/openapi/cmp.yaml): thin, commands go through the pipeline.</summary>
[ApiController]
internal sealed class ClocksController : ControllerBase
{
    [HttpPost("api/cmp/v1/clocks/start")]
    [Authorize(Policy = ClockPermissions.Start)]
    public async Task<IResult> StartAsync([FromBody] ClockStartRequest request, [FromServices] ICommandHandler<StartClock, ClockStartResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new StartClock(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("api/cmp/v1/clocks/pause")]
    [Authorize(Policy = ClockPermissions.Pause)]
    public async Task<IResult> PauseAsync([FromBody] ClockPauseRequest request, [FromServices] ICommandHandler<PauseClock, ClockPauseResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PauseClock(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("api/cmp/v1/clocks/resume")]
    [Authorize(Policy = ClockPermissions.Resume)]
    public async Task<IResult> ResumeAsync([FromBody] ClockResumeRequest request, [FromServices] ICommandHandler<ResumeClock, ClockResumeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ResumeClock(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("api/cmp/v1/clocks/stop")]
    [Authorize(Policy = ClockPermissions.Stop)]
    public async Task<IResult> StopAsync([FromBody] ClockStopRequest request, [FromServices] ICommandHandler<StopClock, ClockStopResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new StopClock(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("api/cmp/v1/clocks/cancel")]
    [Authorize(Policy = ClockPermissions.Cancel)]
    public async Task<IResult> CancelAsync([FromBody] ClockCancelRequest request, [FromServices] ICommandHandler<CancelClock, ClockCancelResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CancelClock(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("api/cmp/v1/clocks/extend")]
    [Authorize(Policy = ClockPermissions.Extend)]
    public async Task<IResult> ExtendAsync([FromBody] ClockExtendRequest request, [FromServices] ICommandHandler<ExtendClock, ClockExtendResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ExtendClock(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>cmp.Clock.query.</summary>
    [HttpGet("api/cmp/v1/clocks/query")]
    [Authorize(Policy = ClockPermissions.Query)]
    public async Task<IResult> QueryAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] Guid? id, [FromQuery] string? subject, [FromQuery] string? code,
        [FromQuery] string? status, [FromQuery] string? deadlineRange, [FromServices] ClockReader reader, CancellationToken cancellationToken)
    {
        var page = await reader.QueryAsync(LegalEntity(), cursor, limit, id, ClockReader.ParseSubject(subject), code, status, deadlineRange, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ClockQueryPage { Items = [.. page.Items.Select(ClockReader.ToItem)], NextCursor = page.NextCursor, Limit = page.Limit });
    }

    /// <summary>cmp.Clock.get: by instance id, or by subject (with optional code and status).</summary>
    [HttpGet("api/cmp/v1/clocks/{id}")]
    [Authorize(Policy = ClockPermissions.Get)]
    public async Task<IResult> GetAsync(
        string id, [FromQuery] string? subject, [FromQuery] string? code, [FromQuery] string? status, [FromQuery] string? deadlineRange,
        [FromServices] ClockReader reader, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity();
        if (Guid.TryParse(id, out var guid))
        {
            var one = await reader.GetByIdAsync(legalEntity, guid, cancellationToken).ConfigureAwait(false);
            return one is null ? ClockNotFound() : Results.Ok(new ClockGetResponse { Instances = [one] });
        }

        var parsed = ClockReader.ParseSubject(subject);
        if (parsed is null)
        {
            return ClockNotFound();
        }

        var page = await reader.QueryAsync(legalEntity, null, ClockReader.MaxPage, null, parsed, code, status, deadlineRange, cancellationToken).ConfigureAwait(false);
        return page.Items.Count == 0 ? ClockNotFound() : Results.Ok(new ClockGetResponse { Instances = [.. page.Items] });
    }

    /// <summary>cmp.ClockDef.list.</summary>
    [HttpGet("api/cmp/v1/clock-defs")]
    [Authorize(Policy = ClockPermissions.DefList)]
#pragma warning disable CA1822 // Not static: ASP.NET does not route static controller methods.
    public IResult ListDefinitions() =>
        Results.Ok(new ClockDefListPage { Items = [.. ClockDefinitions.All.Select(ClockReader.ToItem)], NextCursor = null });
#pragma warning restore CA1822

    /// <summary>cmp.ClockDef.get.</summary>
    [HttpGet("api/cmp/v1/clock-defs/{id}")]
    [Authorize(Policy = ClockPermissions.DefGet)]
    public IResult GetDefinition(string id) =>
        ClockDefinitions.Find(id) is { } definition
            ? Results.Ok(ClockReader.ToGet(definition))
            : HttpResults.Problem(DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The clock definition does not exist."), HttpContext);

    private IResult ClockNotFound() => HttpResults.Problem(DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The clock does not exist in your legal entity."), HttpContext);

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity!.Value);
    }
}
