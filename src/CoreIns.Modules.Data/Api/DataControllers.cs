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

/// <summary>REST facade of the DAT-CORE operations (contracts/openapi/dat.yaml).</summary>
[ApiController]
internal sealed class DataController : ControllerBase
{
    /// <summary>dat.Subject.export.</summary>
    [HttpPost("api/dat/v1/subject/export")]
    [Authorize(Policy = "dat.Subject.export")]
    public async Task<IResult> ExportAsync(
        [FromBody] SubjectExportRequest request,
        [FromServices] ICommandHandler<ExportSubject, SubjectExportResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ExportSubject(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.Erasure.execute.</summary>
    [HttpPost("api/dat/v1/erasure/execute")]
    [Authorize(Policy = "dat.Erasure.execute")]
    public async Task<IResult> EraseAsync(
        [FromBody] ErasureExecuteRequest request,
        [FromServices] ICommandHandler<ExecuteErasure, ErasureExecuteResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ExecuteErasure(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.Restriction.apply.</summary>
    [HttpPost("api/dat/v1/restriction/apply")]
    [Authorize(Policy = "dat.Restriction.apply")]
    public async Task<IResult> RestrictAsync(
        [FromBody] RestrictionApplyRequest request,
        [FromServices] ICommandHandler<ApplyRestriction, RestrictionApplyResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApplyRestriction(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.BiasTest.run.</summary>
    [HttpPost("api/dat/v1/bias-tests/run")]
    [Authorize(Policy = "dat.BiasTest.run")]
    public async Task<IResult> BiasTestAsync(
        [FromBody] BiasTestRunRequest request,
        [FromServices] ICommandHandler<RunBiasTest, BiasTestRunResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RunBiasTest(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.Model.register.</summary>
    [HttpPost("api/dat/v1/models/register")]
    [Authorize(Policy = "dat.Model.register")]
    public async Task<IResult> RegisterModelAsync(
        [FromBody] ModelRegisterRequest request,
        [FromServices] ICommandHandler<RegisterModel, ModelRegisterResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RegisterModel(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>dat.Model.get.</summary>
    [HttpGet("api/dat/v1/models/{id}")]
    [Authorize(Policy = "dat.Model.get")]
    public async Task<IResult> GetModelAsync(string id, [FromServices] ModelReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid) ? await reader.GetAsync(guid, cancellationToken).ConfigureAwait(false) : null;
        return found is null
            ? HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>()
                .ToResult(DomainError.Of(ModuleCode.DAT, "NOT-FOUND", "The model does not exist."), HttpContext)
            : Results.Ok(found);
    }
}

/// <summary>Development-only seed (SL6-E2E-09): writes one encrypted subject fact so a DSAR journey has something to export and erase. 404 elsewhere.</summary>
[ApiController]
[Route("api/dat/v1/dev/subject-facts")]
#pragma warning disable CA1822 // Not static: ASP.NET does not route static controller methods.
internal sealed class DevSubjectFactsController : ControllerBase
{
    /// <summary>Request body of the seed endpoint.</summary>
    public sealed record SeedFact(string SubjectToken, string Category, string Key, string Value);

    [HttpPost]
    [Authorize(Policy = "dat.Erasure.execute")]
    public async Task<IResult> SeedAsync(
        [FromBody] SeedFact fact,
        [FromServices] SubjectFactStore store,
        [FromServices] Microsoft.Extensions.Hosting.IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsDevelopment(environment))
        {
            return Results.NotFound();
        }

        var written = await store.RecordAsync(fact.SubjectToken, fact.Category, fact.Key, fact.Value, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { written });
    }
}
