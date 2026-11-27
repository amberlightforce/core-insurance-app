using CoreIns.Modules.Market.Commands;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Http;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Market.Api;

/// <summary>REST facade of the configuration change requests (BO-MKT-01): maker-checker over country-layer values.</summary>
[ApiController]
[Route("api/mkt/v1/config-change-requests")]
internal sealed class ConfigChangeRequestsController(MarketDbContext db, RequestContext context) : ControllerBase
{
    private static readonly string[] Statuses = ["DRAFT", "PENDING_APPROVAL", "SCHEDULED", "APPLIED", "REJECTED", "WITHDRAWN"];

    /// <summary>mkt.ConfigChangeRequest.create.</summary>
    [HttpPost]
    [Authorize(Policy = "mkt.ConfigChangeRequest.create")]
    public async Task<IResult> CreateAsync([FromBody] ConfigChangeRequestCreateRequest request,
        [FromServices] ICommandHandler<CreateConfigChange, ConfigChangeRequestCreateResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new CreateConfigChange(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>mkt.ConfigChangeRequest.update: DRAFT only, maker only.</summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.update")]
    public async Task<IResult> UpdateAsync(Guid id, [FromBody] ConfigChangeRequestUpdateRequest request,
        [FromServices] ICommandHandler<UpdateConfigChange, ConfigChangeRequestUpdateResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new UpdateConfigChange(id, request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>mkt.ConfigChangeRequest.submit.</summary>
    [HttpPost("submit")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.submit")]
    public async Task<IResult> SubmitAsync([FromBody] ConfigChangeRequestSubmitRequest request,
        [FromServices] ICommandHandler<SubmitConfigChange, ConfigChangeRequestSubmitResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new SubmitConfigChange(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>mkt.ConfigChangeRequest.approve: applies the change in the same transaction.</summary>
    [HttpPost("approve")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.approve")]
    public async Task<IResult> ApproveAsync([FromBody] ConfigChangeRequestApproveRequest request,
        [FromServices] ICommandHandler<ApproveConfigChange, ConfigChangeRequestApproveResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new ApproveConfigChange(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>mkt.ConfigChangeRequest.reject.</summary>
    [HttpPost("reject")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.reject")]
    public async Task<IResult> RejectAsync([FromBody] ConfigChangeRequestRejectRequest request,
        [FromServices] ICommandHandler<RejectConfigChange, ConfigChangeRequestRejectResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new RejectConfigChange(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>mkt.ConfigChangeRequest.withdraw: maker only.</summary>
    [HttpPost("withdraw")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.withdraw")]
    public async Task<IResult> WithdrawAsync([FromBody] ConfigChangeRequestWithdrawRequest request,
        [FromServices] ICommandHandler<WithdrawConfigChange, ConfigChangeRequestWithdrawResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new WithdrawConfigChange(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>mkt.ConfigChangeRequest.schedule: sets the effective instant of a DRAFT (maker only); the checker approves it with the content.</summary>
    [HttpPost("schedule")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.schedule")]
    public async Task<IResult> ScheduleAsync([FromBody] ConfigChangeRequestScheduleRequest request,
        [FromServices] ICommandHandler<ScheduleConfigChange, ConfigChangeRequestScheduleResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new ScheduleConfigChange(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>mkt.ConfigChangeRequest.rebase: maker only; moves the request onto the newest state and back to DRAFT.</summary>
    [HttpPost("rebase")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.rebase")]
    public async Task<IResult> RebaseAsync([FromBody] ConfigChangeRequestRebaseRequest request,
        [FromServices] ICommandHandler<RebaseConfigChange, ConfigChangeRequestRebaseResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new RebaseConfigChange(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>mkt.ConfigChangeRequest.preview: diff and resulting hash against the newest state; writes nothing.</summary>
    [HttpPost("preview")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.preview")]
    public async Task<IResult> PreviewAsync([FromBody] ConfigChangeRequestPreviewRequest request,
        [FromServices] ICommandHandler<PreviewConfigChange, ConfigChangeRequestPreviewResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new PreviewConfigChange(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>mkt.ConfigChangeRequest.import: validates every key and creates a DRAFT request; never applies.</summary>
    [HttpPost("import")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.import")]
    public async Task<IResult> ImportAsync([FromBody] ConfigChangeRequestImportRequest request,
        [FromServices] ICommandHandler<ImportConfigChange, ConfigChangeRequestImportResponse> handler, CancellationToken ct) =>
        (await handler.HandleAsync(new ImportConfigChange(request), ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private IQueryable<ConfigChangeRequestRow> Rows() => db.ConfigChangeRequests.AsNoTracking().Include(r => r.Items).Where(r =>
        db.LegalEntities.Any(e => e.LegalEntityId == r.LegalEntityId && e.Code == context.LegalEntity!.Value.Value));

    /// <summary>mkt.ConfigChangeRequest.get.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = "mkt.ConfigChangeRequest.get")]
    public async Task<IResult> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await Rows().SingleOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        return row is null ? Results.NotFound() : Results.Ok(ConfigChangeSupport.View(row));
    }

    /// <summary>mkt.ConfigChangeRequest.list: newest first, keyset paging by id.</summary>
    [HttpGet]
    [Authorize(Policy = "mkt.ConfigChangeRequest.list")]
    public async Task<IResult> ListAsync([FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var query = Rows();
        if (status is not null)
        {
            if (!Statuses.Contains(status))
            {
                return Results.BadRequest();
            }

            query = query.Where(r => r.Status == status);
        }

        if (cursor is not null)
        {
            if (!Guid.TryParse(cursor, out var after))
            {
                return Results.BadRequest();
            }

            query = query.Where(r => r.Id.CompareTo(after) < 0);
        }

        var rows = await query.OrderByDescending(r => r.Id).Take(take + 1).ToListAsync(ct).ConfigureAwait(false);
        return Results.Ok(new ConfigChangeRequestListPage
        {
            Items = [.. rows.Take(take).Select(ConfigChangeSupport.View)],
            NextCursor = rows.Count > take ? rows[take - 1].Id.ToString("D") : null,
            Limit = take,
        });
    }
}
