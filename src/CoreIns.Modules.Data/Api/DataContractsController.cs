using CoreIns.Modules.Data.Commands;
using CoreIns.Modules.Data.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Data.Api;

/// <summary>REST facade of the data-contract registry, raw-layer replay and quarantine release (BO-DAT-01; contracts/openapi/dat.yaml).</summary>
[ApiController]
internal sealed class DataContractsController : ControllerBase
{
    /// <summary>dat.DataContract.register.</summary>
    [HttpPost("api/dat/v1/data-contracts/register")]
    [Authorize(Policy = "dat.DataContract.register")]
    public async Task<IResult> RegisterAsync(
        [FromBody] DataContractRegisterRequest request,
        [FromServices] ICommandHandler<RegisterDataContract, DataContractRegisterResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RegisterDataContract(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.DataContract.get.</summary>
    [HttpGet("api/dat/v1/data-contracts/{id}")]
    [Authorize(Policy = "dat.DataContract.get")]
    public async Task<IResult> GetAsync(string id, [FromQuery] string? producer, [FromServices] DataContractReader reader, CancellationToken cancellationToken)
    {
        var found = await reader.GetAsync(id, producer, cancellationToken).ConfigureAwait(false);
        return found is null
            ? HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>()
                .ToResult(DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "The data contract does not exist."), HttpContext)
            : Results.Ok(found);
    }

    /// <summary>dat.DataContract.list.</summary>
    [HttpGet("api/dat/v1/data-contracts")]
    [Authorize(Policy = "dat.DataContract.list")]
    public async Task<IResult> ListAsync(
        [FromQuery] Guid? id, [FromQuery] string? producer, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] DataContractReader reader, CancellationToken cancellationToken) =>
        (await reader.ListAsync(id, producer, cursor, limit, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.Ingestion.replay (202: the work is done inline and the job id is the receipt).</summary>
    [HttpPost("api/dat/v1/ingestion/replay")]
    [Authorize(Policy = "dat.Ingestion.replay")]
    public async Task<IResult> ReplayAsync(
        [FromBody] IngestionReplayRequest request,
        [FromServices] ICommandHandler<ReplayIngestion, IngestionReplayResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ReplayIngestion(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Json(result.Value, statusCode: StatusCodes.Status202Accepted) : result.ToHttpResult(HttpContext);
    }

    /// <summary>dat.Quarantine.release.</summary>
    [HttpPost("api/dat/v1/quarantine/release")]
    [Authorize(Policy = "dat.Quarantine.release")]
    public async Task<IResult> ReleaseAsync(
        [FromBody] QuarantineReleaseRequest request,
        [FromServices] ICommandHandler<ReleaseQuarantine, QuarantineReleaseResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReleaseQuarantine(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
