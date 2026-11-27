using CoreIns.Modules.Migration.Commands;
using CoreIns.Modules.Migration.Services.Mapping;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Migration.Api;

/// <summary>REST facade of mapping specs (BO-MIG-04; contracts/openapi/mig.yaml mig.Mapping.*).</summary>
[ApiController]
[Route("api/mig/v1/mapping")]
internal sealed class MappingController : ControllerBase
{
    [HttpPost("specs")]
    [Authorize(Policy = "mig.Mapping.create")]
    public async Task<IResult> CreateAsync([FromBody] MappingCreateBody body, [FromServices] ICommandHandler<CreateMappingSpec, MappingSpecView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CreateMappingSpec(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("specs")]
    [Authorize(Policy = "mig.Mapping.list")]
    public async Task<IResult> ListAsync([FromQuery] string? name, [FromQuery] string? status, [FromServices] MappingService service, CancellationToken cancellationToken) =>
        (await service.ListAsync(name, status, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpGet("specs/{id:guid}")]
    [Authorize(Policy = "mig.Mapping.get")]
    public async Task<IResult> GetAsync(Guid id, [FromServices] MappingService service, CancellationToken cancellationToken) =>
        (await service.GetAsync(id, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("publish")]
    [Authorize(Policy = "mig.Mapping.publish")]
    public async Task<IResult> PublishAsync([FromBody] MappingPublishBody body, [FromServices] ICommandHandler<PublishMappingSpec, MappingSpecView> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PublishMappingSpec(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("preview")]
    [Authorize(Policy = "mig.Mapping.preview")]
    public async Task<IResult> PreviewAsync([FromBody] MappingPreviewBody body, [FromServices] MappingService service, CancellationToken cancellationToken) =>
        (await service.PreviewAsync(body, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
