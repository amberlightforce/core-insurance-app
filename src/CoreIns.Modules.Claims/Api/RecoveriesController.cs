using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Commands.Recoveries;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Claims.Api;

/// <summary>Permission names of the recovery operations (x-permission).</summary>
internal static class ClaimsRecoveryPermissions
{
    public const string Create = "clm.Recovery.create";
    public const string List = "clm.Recovery.list";
    public const string Get = "clm.Recovery.get";
    public const string RecordMilestone = "clm.Recovery.recordMilestone";
    public const string Demand = "clm.Recovery.demand";
    public const string WriteOff = "clm.Recovery.writeOff";
}

/// <summary>
/// REST facade of <c>clm.Recovery.*</c> (SL4-CLM-RECOVERY-OPS). Cash is never accepted here: a client creates cases, records
/// milestones, demands through a BIL receivable and writes off; recoveries are posted from BIL evidence only (D-SL4-06).
/// </summary>
[ApiController]
[Route("api/clm/v1/recoveries")]
internal sealed class RecoveriesController : ControllerBase
{
    [HttpPost("")]
    [Authorize(Policy = ClaimsRecoveryPermissions.Create)]
    public async Task<IResult> CreateAsync(
        [FromBody] RecoveryCreateRequest request, [FromServices] ICommandHandler<CreateRecovery, RecoveryCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateRecovery(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/clm/v1/recoveries/{result.Value.Recovery.RecoveryId.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    [HttpGet("")]
    [Authorize(Policy = ClaimsRecoveryPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? claimId, [FromQuery] string? state, [FromQuery] string? type, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] RecoveryReader reader, [FromServices] FinancialsReader financials, CancellationToken cancellationToken)
    {
        Guid last = default;
        if (!Guid.TryParse(claimId, out var claim) || !ClaimReader.TryPage(limit, out var take)
            || (cursor is not null && !Guid.TryParse(cursor, out last))
            || (state is not null && !Enum.GetValues<RecoveryState>().Any(s => Codes.Of(s) == state))
            || (type is not null && !Enum.GetValues<RecoveryType>().Any(s => Codes.Of(s) == type)))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.CLM, "VALIDATION", "Invalid claim, state, type, cursor or limit."), HttpContext);
        }

        var entity = ClaimsQueryContext.LegalEntity(HttpContext.RequestServices);
        var id = new ClaimId(claim);
        return await financials.ClaimExistsAsync(entity, id, cancellationToken).ConfigureAwait(false)
            ? Results.Ok(await reader.ListAsync(entity, id, state, type, cursor is null ? null : last, take, cancellationToken).ConfigureAwait(false))
            : HttpResults.Problem(ClaimSupport.NotFound("claim"), HttpContext);
    }

    [HttpGet("{id}")]
    [Authorize(Policy = ClaimsRecoveryPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] RecoveryReader reader, CancellationToken cancellationToken)
    {
        var row = Guid.TryParse(id, out var guid)
            ? await reader.GetRowAsync(ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), new RecoveryId(guid), cancellationToken).ConfigureAwait(false)
            : null;
        return row is null
            ? HttpResults.Problem(ClaimSupport.NotFound("recovery"), HttpContext)
            : Results.Ok(new RecoveryGetResponse { Recovery = await reader.ViewAsync(row, cancellationToken).ConfigureAwait(false) });
    }

    [HttpPost("record-milestone")]
    [Authorize(Policy = ClaimsRecoveryPermissions.RecordMilestone)]
    public async Task<IResult> RecordMilestoneAsync(
        [FromBody] RecoveryRecordMilestoneRequest request, [FromServices] ICommandHandler<RecordRecoveryMilestone, RecoveryRecordMilestoneResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordRecoveryMilestone(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("demand")]
    [Authorize(Policy = ClaimsRecoveryPermissions.Demand)]
    public async Task<IResult> DemandAsync(
        [FromBody] RecoveryDemandRequest request, [FromServices] ICommandHandler<DemandRecovery, RecoveryDemandResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DemandRecovery(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("write-off")]
    [Authorize(Policy = ClaimsRecoveryPermissions.WriteOff)]
    public async Task<IResult> WriteOffAsync(
        [FromBody] RecoveryWriteOffRequest request, [FromServices] ICommandHandler<WriteOffRecovery, RecoveryWriteOffResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new WriteOffRecovery(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
