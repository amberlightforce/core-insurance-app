using CoreIns.Modules.Market.Commands;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Http;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Market.Api;

[ApiController]
[Route("api/mkt/v1/packs")]
internal sealed class PackCommandsController : ControllerBase
{
    [HttpPost("rollback")]
    [Authorize(Policy = "mkt.Pack.rollback")]
    public async Task<IResult> RollbackAsync([FromBody] PackRollbackRequest request,
        [FromServices] ICommandHandler<RequestPackRollback, PackRollbackResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new RequestPackRollback(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("schedule-activation")]
    [Authorize(Policy = "mkt.Pack.scheduleActivation")]
    public async Task<IResult> ActivateAsync([FromBody] PackScheduleActivationRequest request,
        [FromServices] ICommandHandler<RequestPackActivation, PackScheduleActivationResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new RequestPackActivation(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);
}

[ApiController]
[Route("api/mkt/v1/pack-activations")]
internal sealed class PackActivationsController(MarketDbContext db, RequestContext context) : ControllerBase
{
    [HttpPost("decide")]
    [Authorize(Policy = "mkt.PackActivation.decide")]
    public async Task<IResult> DecideAsync([FromBody] PackActivationDecideRequest request,
        [FromServices] ICommandHandler<DecidePackActivation, PackActivationDecideResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new DecidePackActivation(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private IQueryable<PackActivationRow> Rows() => db.PackActivations.AsNoTracking().Where(a =>
        db.LegalEntities.Any(e => e.LegalEntityId == a.LegalEntityId && e.Code == context.LegalEntity!.Value.Value));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "mkt.PackActivation.get")]
    public async Task<IResult> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await Rows().SingleOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
        return row is null ? Results.NotFound() : Results.Ok(ActivationSupport.View(row, context.LegalEntity!.Value.Value));
    }

    [HttpGet]
    [Authorize(Policy = "mkt.PackActivation.list")]
    public async Task<IResult> ListAsync([FromQuery] string? pack, [FromQuery] string? legalEntity, [FromQuery] string? status,
        [FromQuery] string? kind, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var query = Rows();
        if (legalEntity is not null && legalEntity != context.LegalEntity!.Value.Value) query = query.Where(a => false);
        if (pack is not null) query = query.Where(a => a.PackId == pack);
        if (status is not null) query = query.Where(a => a.Status == status);
        if (kind is not null) query = query.Where(a => a.Kind == kind);
        if (cursor is not null)
        {
            if (!Guid.TryParse(cursor, out var after)) return Results.BadRequest();
            query = query.Where(a => a.Id.CompareTo(after) < 0);
        }
        var rows = await query.OrderByDescending(a => a.Id).Take(take + 1).ToListAsync(ct).ConfigureAwait(false);
        return Results.Ok(new PackActivationListPage { Items = rows.Take(take).Select(a => ActivationSupport.View(a, context.LegalEntity!.Value.Value)).ToList(), NextCursor = rows.Count > take ? rows[take - 1].Id.ToString("D") : null, Limit = take });
    }
}
