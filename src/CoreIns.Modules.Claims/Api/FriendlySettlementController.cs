using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Commands.FriendlySettlement;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Claims.Api;

/// <summary>Permission names of the FS case operations (x-permission).</summary>
internal static class FriendlySettlementPermissions
{
    public const string List = "clm.FriendlySettlement.list";
    public const string Get = "clm.FriendlySettlement.get";
    public const string Evaluate = "clm.FriendlySettlement.evaluate";
    public const string OpenOwn = "clm.FriendlySettlement.openOwnSettlement";
    public const string Submit = "clm.FriendlySettlement.submit";
    public const string DevNotify = "clm.DevFsClearing.notify";
}

/// <summary>Reads FS cases (clm.FriendlySettlement.get / list).</summary>
internal sealed class FsCaseReader(ClaimsDbContext db, FinancialsReader financials)
{
    public async Task<FsCaseView?> GetAsync(LegalEntityId entity, Guid id, CancellationToken cancellationToken)
    {
        var row = await db.FsCases.AsNoTracking().SingleOrDefaultAsync(c => c.LegalEntityId == entity && c.FsCaseId == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : await FsCases.ViewAsync(db, financials, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FsListPage> ListAsync(
        LegalEntityId entity, Guid? claimId, string? state, string? role, Guid? after, int take, CancellationToken cancellationToken)
    {
        var query = db.FsCases.AsNoTracking().Where(c => c.LegalEntityId == entity);
        if (claimId is { } claim)
        {
            query = query.Where(c => c.ClaimId == new ClaimId(claim));
        }

        if (state is not null)
        {
            query = query.Where(c => c.Status == state);
        }

        if (role is not null)
        {
            query = query.Where(c => c.Role == role);
        }

        if (after is { } last)
        {
            query = query.Where(c => c.FsCaseId.CompareTo(last) > 0);
        }

        var rows = await query.OrderBy(c => c.FsCaseId).Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<FsCaseView>();
        foreach (var row in rows.Take(take))
        {
            items.Add(await FsCases.ViewAsync(db, financials, row, cancellationToken).ConfigureAwait(false));
        }

        return new FsListPage { Items = items, NextCursor = rows.Count > take ? rows[take - 1].FsCaseId.ToString() : null, Limit = take };
    }
}

/// <summary>REST facade of the Friendly Settlement case (SL4-CLM-FS-CASE). The statement operations belong to SL4-CLM-FS-STATEMENT.</summary>
[ApiController]
[Route("api/clm/v1/friendly-settlements")]
internal sealed class FriendlySettlementController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = FriendlySettlementPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? claimId, [FromQuery] string? state, [FromQuery] string? role, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] FsCaseReader reader, CancellationToken cancellationToken)
    {
        Guid? claim = null;
        Guid? after = null;
        if (claimId is not null && Guid.TryParse(claimId, out var parsedClaim))
        {
            claim = parsedClaim;
        }

        if (cursor is not null && Guid.TryParse(cursor, out var parsedCursor))
        {
            after = parsedCursor;
        }

        if ((claimId is not null && claim is null) || (cursor is not null && after is null) || !ClaimReader.TryPage(limit, out var take)
            || (state is not null && !Enum.GetValues<FsCaseState>().Any(s => FsCases.StateCode(s) == state))
            || (role is not null && role is not (FsCases.OwnRole or FsCases.AtFaultRole)))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.CLM, "VALIDATION", "Invalid claim, state, role, cursor or limit."), HttpContext);
        }

        return Results.Ok(await reader.ListAsync(ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), claim, state, role, after, take, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("{id}")]
    [Authorize(Policy = FriendlySettlementPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] FsCaseReader reader, CancellationToken cancellationToken)
    {
        var view = Guid.TryParse(id, out var caseId)
            ? await reader.GetAsync(ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), caseId, cancellationToken).ConfigureAwait(false)
            : null;
        return view is null ? HttpResults.Problem(ClaimSupport.NotFound("Friendly Settlement case"), HttpContext) : Results.Ok(new FsGetResponse { FsCase = view });
    }

    [HttpPost("evaluate")]
    [Authorize(Policy = FriendlySettlementPermissions.Evaluate)]
    public async Task<IResult> EvaluateAsync(
        [FromBody] FsEvaluateRequest request, [FromServices] ICommandHandler<EvaluateFriendlySettlement, FsEvaluateResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new EvaluateFriendlySettlement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("open-own-settlement")]
    [Authorize(Policy = FriendlySettlementPermissions.OpenOwn)]
    public async Task<IResult> OpenOwnAsync(
        [FromBody] FsOpenOwnSettlementRequest request, [FromServices] ICommandHandler<OpenOwnSettlement, FsOpenOwnSettlementResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new OpenOwnSettlement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("submit")]
    [Authorize(Policy = FriendlySettlementPermissions.Submit)]
    public async Task<IResult> SubmitAsync(
        [FromBody] FsSubmitRequest request, [FromServices] ICommandHandler<SubmitFriendlySettlement, FsSubmitResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SubmitFriendlySettlement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}

/// <summary>
/// Development-only inbound clearing endpoint (D-SL4-18), like the dev clock: <c>POST /api/clm/v1/dev/fs-clearing/notifications</c>
/// takes the body of the stub notification test format (the raw JSON, optional <c>?format=</c>). The Host maps it outside Production only.
/// </summary>
public static class DevFsClearingEndpoints
{
    /// <summary>Default format code: the Greek stub's JSON test format.</summary>
    public const string StubNotificationFormat = "coreins.fs-notification.stub+json";

    /// <summary>Maps the endpoint. Call only outside Production.</summary>
    public static void MapDevFsClearing(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(
            "/api/clm/v1/dev/fs-clearing/notifications",
            async (HttpContext http, string? format, CoreIns.Platform.Commands.ICommandHandler<ReceiveFsNotification, FsNotificationReceived> handler, CancellationToken cancellationToken) =>
            {
                using var buffer = new MemoryStream();
                await http.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                var result = await handler.HandleAsync(new ReceiveFsNotification(format ?? StubNotificationFormat, buffer.ToArray()), cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult(http);
            })
            .RequireAuthorization(FriendlySettlementPermissions.DevNotify);
    }
}
