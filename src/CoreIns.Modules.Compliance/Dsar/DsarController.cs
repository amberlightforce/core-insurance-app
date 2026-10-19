using CoreIns.Modules.Compliance.Api;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Compliance.Dsar;

/// <summary>REST facade of <c>cmp.Dsar.*</c> and <c>cmp.DisclosureLog.*</c> (contracts/openapi/cmp.yaml): thin, commands go through the pipeline.</summary>
[ApiController]
#pragma warning disable CA1822 // Not static: ASP.NET does not route static controller methods.
internal sealed class DsarController : ControllerBase
{
    private const string P = "cmp.Dsar.";

    private async Task<IResult> RunHandler<TCommand, TResponse>(TCommand command, ICommandHandler<TCommand, TResponse> handler, CancellationToken ct)
        where TCommand : ICommand<TResponse> =>
        (await handler.HandleAsync(command, ct).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("api/cmp/v1/dsar")]
    [Authorize(Policy = P + "create")]
    public Task<IResult> CreateAsync([FromBody] DsarCreateRequest request, [FromServices] ICommandHandler<CreateDsar, DsarCreateResponse> handler, CancellationToken ct) =>
        RunHandler(new CreateDsar(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/verify-identity")]
    [Authorize(Policy = P + "verifyIdentity")]
    public Task<IResult> VerifyAsync([FromBody] DsarVerifyIdentityRequest request, [FromServices] ICommandHandler<VerifyDsarIdentity, DsarVerifyIdentityResponse> handler, CancellationToken ct) =>
        RunHandler(new VerifyDsarIdentity(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/start-fan-out")]
    [Authorize(Policy = P + "startFanOut")]
    public Task<IResult> StartFanOutAsync([FromBody] DsarStartFanOutRequest request, [FromServices] ICommandHandler<StartDsarFanOut, DsarStartFanOutResponse> handler, CancellationToken ct) =>
        RunHandler(new StartDsarFanOut(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/retry-task")]
    [Authorize(Policy = P + "retryTask")]
    public Task<IResult> RetryAsync([FromBody] DsarRetryTaskRequest request, [FromServices] ICommandHandler<RetryDsarTask, DsarRetryTaskResponse> handler, CancellationToken ct) =>
        RunHandler(new RetryDsarTask(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/decide-item")]
    [Authorize(Policy = P + "decideItem")]
    public Task<IResult> DecideAsync([FromBody] DsarDecideItemRequest request, [FromServices] ICommandHandler<DecideDsarItem, DsarDecideItemResponse> handler, CancellationToken ct) =>
        RunHandler(new DecideDsarItem(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/approve")]
    [Authorize(Policy = P + "approve")]
    public Task<IResult> ApproveAsync([FromBody] DsarApproveRequest request, [FromServices] ICommandHandler<ApproveDsar, DsarApproveResponse> handler, CancellationToken ct) =>
        RunHandler(new ApproveDsar(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/extend")]
    [Authorize(Policy = P + "extend")]
    public Task<IResult> ExtendAsync([FromBody] DsarExtendRequest request, [FromServices] ICommandHandler<ExtendDsar, DsarExtendResponse> handler, CancellationToken ct) =>
        RunHandler(new ExtendDsar(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/refuse")]
    [Authorize(Policy = P + "refuse")]
    public Task<IResult> RefuseAsync([FromBody] DsarRefuseRequest request, [FromServices] ICommandHandler<RefuseDsar, DsarRefuseResponse> handler, CancellationToken ct) =>
        RunHandler(new RefuseDsar(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/export")]
    [Authorize(Policy = P + "export")]
    public Task<IResult> ExportAsync([FromBody] DsarExportRequest request, [FromServices] ICommandHandler<ExportDsar, DsarExportResponse> handler, CancellationToken ct) =>
        RunHandler(new ExportDsar(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/restrict")]
    [Authorize(Policy = P + "restrict")]
    public Task<IResult> RestrictAsync([FromBody] DsarRestrictRequest request, [FromServices] ICommandHandler<RestrictDsar, DsarRestrictResponse> handler, CancellationToken ct) =>
        RunHandler(new RestrictDsar(request), handler, ct);

    [HttpPost("api/cmp/v1/dsar/erase")]
    [Authorize(Policy = P + "erase")]
    public Task<IResult> EraseAsync([FromBody] DsarEraseRequest request, [FromServices] ICommandHandler<EraseDsar, DsarEraseResponse> handler, CancellationToken ct) =>
        RunHandler(new EraseDsar(request), handler, ct);

    [HttpGet("api/cmp/v1/dsar/query")]
    [Authorize(Policy = P + "query")]
    public async Task<IResult> QueryAsync([FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] DsarReader reader, CancellationToken ct)
    {
        var (items, next, take) = await reader.QueryAsync(cursor, limit, ct).ConfigureAwait(false);
        return Results.Ok(new DsarQueryPage
        {
            Items = [.. items.Select(c => { var (r, t, p) = DsarViews.Of(c); return new DsarQueryItem { Request = r, Tasks = t, Package = p }; })],
            NextCursor = next,
            Limit = take,
        });
    }

    [HttpGet("api/cmp/v1/dsar/{id}")]
    [Authorize(Policy = P + "get")]
    public async Task<IResult> GetAsync(string id, [FromServices] DsarReader reader, CancellationToken ct)
    {
        var found = Guid.TryParse(id, out var guid) ? await reader.GetAsync(guid, ct).ConfigureAwait(false) : null;
        if (found is null)
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.CMP, "NOT-FOUND", "The data-subject request does not exist in your legal entity."), HttpContext);
        }

        var (r, t, p) = DsarViews.Of(found);
        return Results.Ok(new DsarGetResponse { Request = r, Tasks = t, Package = p });
    }

    [HttpPost("api/cmp/v1/disclosure-logs/record")]
    [Authorize(Policy = "cmp.DisclosureLog.record")]
    public Task<IResult> RecordAsync([FromBody] DisclosureLogRecordRequest request, [FromServices] ICommandHandler<RecordDisclosure, DisclosureLogRecordResponse> handler, CancellationToken ct) =>
        RunHandler(new RecordDisclosure(request), handler, ct);

    [HttpGet("api/cmp/v1/disclosure-logs/query")]
    [Authorize(Policy = "cmp.DisclosureLog.query")]
    public async Task<IResult> QueryDisclosuresAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] Guid? partyId, [FromServices] DsarReader reader, CancellationToken ct)
    {
        var (rows, next, take) = await reader.QueryDisclosuresAsync(cursor, limit, partyId, ct).ConfigureAwait(false);
        return Results.Ok(new DisclosureLogQueryPage
        {
            Items = [.. rows.Select(r => new DisclosureLogQueryItem { Entries = [DsarReader.Entry(r)] })],
            NextCursor = next,
            Limit = take,
        });
    }
}
