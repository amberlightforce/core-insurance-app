using System.Globalization;
using CoreIns.Modules.Product.Commands;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Product.Api;

/// <summary>Permissions of the PFC operations (the operations' <c>x-permission</c>), mapped to roles in <c>Platform:Permissions:Grants</c>.</summary>
internal static class ProductPermissions
{
    public const string Resolve = "pfc.ProductVersion.resolve";
    public const string Import = "pfc.ProductVersion.import";
    public const string Fallback = "pfc.ProductVersion.fallback";
    public const string DecideFallback = "pfc.ProductVersion.decideFallback";
    public const string ArtifactGet = "pfc.Artifact.get";
    public const string CatalogueGet = "pfc.Catalogue.get";
    public const string CatalogueGetItem = "pfc.Catalogue.getItem";
    public const string ChargeTypeList = "pfc.ChargeType.list";
    public const string QuestionSetGet = "pfc.QuestionSet.get";
    public const string QuestionSetEvaluate = "pfc.QuestionSet.evaluate";
}

/// <summary>Problem Details from a domain error.</summary>
internal static class ProductHttp
{
    public static IResult ProblemOf(this ControllerBase controller, DomainError error) =>
        controller.HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, controller.HttpContext);

    public static IResult Respond<T>(this ControllerBase controller, Result<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : controller.ProblemOf(result.Error);
}

/// <summary>REST facade of <c>pfc.ProductVersion.resolve</c> (a query sent as POST) and <c>import</c> (the seed and authoring load path).</summary>
[ApiController]
[Route("api/pfc/v1/product-versions")]
internal sealed class ProductVersionsController : ControllerBase
{
    /// <summary>pfc.ProductVersion.resolve: the Locked version in force on <c>validAt</c> (default today).</summary>
    [HttpPost("resolve")]
    [SkipIdempotency]
    [Authorize(Policy = ProductPermissions.Resolve)]
    public async Task<IResult> ResolveAsync(
        [FromBody] ProductVersionResolveRequest request,
        [FromQuery] string? validAt,
        [FromQuery] string? knownAt,
        [FromServices] ProductReader reader,
        [FromServices] IClock clock,
        CancellationToken cancellationToken)
    {
        if (!TryTime(clock, validAt, knownAt, out var valid, out var known))
        {
            return this.ProblemOf(DomainError.Of(ModuleCode.PFC, "VALIDATION", "validAt must be a date or instant, knownAt an instant."));
        }

        return this.Respond(await reader.ResolveAsync(request, valid, known, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>pfc.ProductVersion.import → 201 (created) or 200 (the same version and artefact already existed).</summary>
    [HttpPost("import")]
    [Authorize(Policy = ProductPermissions.Import)]
    public async Task<IResult> ImportAsync(
        [FromBody] ProductVersionImportRequest request,
        [FromServices] ICommandHandler<ImportProductVersion, ProductVersionImportResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ImportProductVersion(request), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return this.ProblemOf(result.Error);
        }

        return result.Value.Created
            ? Results.Created($"/api/pfc/v1/product-versions/resolve", result.Value)
            : Results.Ok(result.Value);
    }

    /// <summary>pfc.ProductVersion.fallback: the maker's emergency fall-back request (dry run previews). The server derives source, number and dates.</summary>
    [HttpPost("fallback")]
    [Authorize(Policy = ProductPermissions.Fallback)]
    public async Task<IResult> FallbackAsync(
        [FromBody] ProductVersionEmergencyFallbackRequest request,
        [FromServices] ICommandHandler<RequestFallback, ProductVersionEmergencyFallbackResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new RequestFallback(request), cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.ProductVersion.decideFallback: the checker's decision; APPROVE executes the fall-back in the same transaction.</summary>
    [HttpPost("decide-fallback")]
    [Authorize(Policy = ProductPermissions.DecideFallback)]
    public async Task<IResult> DecideFallbackAsync(
        [FromBody] ProductVersionDecideFallbackRequest request,
        [FromServices] ICommandHandler<DecideFallback, ProductVersionDecideFallbackResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new DecideFallback(request), cancellationToken).ConfigureAwait(false));

    private static bool TryTime(IClock clock, string? validAt, string? knownAt, out BusinessDate valid, out Instant known)
    {
        var now = clock.Now;
        valid = Domain.FallbackPlanner.AthensDate(now);
        known = now;
        if (validAt is not null)
        {
            if (Instant.TryParse(validAt, out var instant))
            {
                valid = Domain.FallbackPlanner.AthensDate(instant);
            }
            else if (DateOnly.TryParseExact(validAt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                valid = new BusinessDate(date);
            }
            else
            {
                return false;
            }
        }

        return knownAt is null || Instant.TryParse(knownAt, out known);
    }
}

/// <summary>REST facade of <c>pfc.Artifact.get</c>.</summary>
[ApiController]
[Route("api/pfc/v1/artifacts")]
internal sealed class ArtifactsController : ControllerBase
{
    /// <summary>pfc.Artifact.get: the canonical JSON artefact with this hash (<c>parts</c> is not supported yet).</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = ProductPermissions.ArtifactGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] CatalogueQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.GetArtefactAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? this.ProblemOf(result.Error)
            : Results.Ok(new ArtifactGetResponse { CanonicalJsonArtefact = result.Value.ToElement() });
    }
}

/// <summary>REST facade of <c>pfc.Catalogue.get</c> and <c>getItem</c>.</summary>
[ApiController]
[Route("api/pfc/v1/catalogue")]
internal sealed class CatalogueController : ControllerBase
{
    /// <summary>pfc.Catalogue.get: coverages, elements and finals of an artefact.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = ProductPermissions.CatalogueGet)]
    public async Task<IResult> GetAsync(
        string id, [FromQuery] string? scope, [FromQuery] string? code, [FromServices] CatalogueQueries queries, CancellationToken cancellationToken) =>
        this.Respond(await queries.GetCatalogueAsync(id, scope, code, cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.Catalogue.getItem: one coverage or element by code.</summary>
    [HttpGet("get-item")]
    [Authorize(Policy = ProductPermissions.CatalogueGetItem)]
    public async Task<IResult> GetItemAsync(
        [FromQuery] string? hash, [FromQuery] string? scope, [FromQuery] string? code, [FromServices] CatalogueQueries queries, CancellationToken cancellationToken) =>
        this.Respond(await queries.GetItemAsync(hash ?? string.Empty, scope, code, cancellationToken).ConfigureAwait(false));
}

/// <summary>REST facade of <c>pfc.ChargeType.list</c>.</summary>
[ApiController]
[Route("api/pfc/v1/charge-types")]
internal sealed class ChargeTypesController : ControllerBase
{
    /// <summary>pfc.ChargeType.list: the charge-type catalogue of an artefact.</summary>
    [HttpGet]
    [Authorize(Policy = ProductPermissions.ChargeTypeList)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? hash, [FromQuery] string? filters, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] CatalogueQueries queries, CancellationToken cancellationToken)
    {
        Sha256Hash? parsed = Sha256Hash.TryParse(hash, out var value) ? value : null;
        return this.Respond(await queries.ListChargeTypesAsync(parsed, filters, cursor, limit, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>REST facade of <c>pfc.QuestionSet.get</c> and <c>evaluate</c> (answers travel in the body only, D-SLC-05).</summary>
[ApiController]
[Route("api/pfc/v1/question-sets")]
internal sealed class QuestionSetsController : ControllerBase
{
    /// <summary>pfc.QuestionSet.get: the question set of an artefact.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = ProductPermissions.QuestionSetGet)]
    public async Task<IResult> GetAsync(
        string id, [FromQuery] string? set, [FromServices] CatalogueQueries queries, CancellationToken cancellationToken) =>
        this.Respond(await queries.GetQuestionSetAsync(id, set, cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.QuestionSet.evaluate: a pure query sent as POST.</summary>
    [HttpPost("evaluate")]
    [SkipIdempotency]
    [Authorize(Policy = ProductPermissions.QuestionSetEvaluate)]
    public async Task<IResult> EvaluateAsync(
        [FromBody] QuestionSetEvaluateRequest request, [FromServices] CatalogueQueries queries, CancellationToken cancellationToken) =>
        this.Respond(await queries.EvaluateQuestionSetAsync(request, cancellationToken).ConfigureAwait(false));
}
