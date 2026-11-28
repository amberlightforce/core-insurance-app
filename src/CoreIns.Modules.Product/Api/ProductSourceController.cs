using CoreIns.Modules.Product.Commands;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Queries;
using CoreIns.Platform.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Product.Api;

/// <summary>REST facade of <c>pfc.ProductVersion.diff</c> and <c>export</c> (BO-PFC-03).</summary>
[ApiController]
[Route("api/pfc/v1/product-versions")]
internal sealed class ProductVersionSourceController : ControllerBase
{
    /// <summary>pfc.ProductVersion.diff: item-level changes between two Locked versions of a product, grouped by area.</summary>
    [HttpGet("diff")]
    [Authorize(Policy = ProductPermissions.Diff)]
    public async Task<IResult> DiffAsync(
        [FromQuery] string? product, [FromQuery] string? from, [FromQuery] string? to, [FromServices] ProductSourceQueries queries, CancellationToken cancellationToken) =>
        this.Respond(await queries.DiffAsync(product, from, to, cancellationToken).ConfigureAwait(false));

    /// <summary>pfc.ProductVersion.export: the canonical definition of a Locked version as YAML (default) or JSON text.</summary>
    [HttpGet("export")]
    [Authorize(Policy = ProductPermissions.Export)]
    public async Task<IResult> ExportAsync(
        [FromQuery] string? product, [FromQuery] string? version, [FromQuery] string? format, [FromServices] ProductSourceQueries queries, CancellationToken cancellationToken) =>
        this.Respond(await queries.ExportAsync(product, version, format, cancellationToken).ConfigureAwait(false));
}

/// <summary>REST facade of <c>pfc.ProductImport.import</c> (BO-PFC-03).</summary>
[ApiController]
[Route("api/pfc/v1/product-imports")]
internal sealed class ProductImportsController : ControllerBase
{
    /// <summary>pfc.ProductImport.import: a YAML or JSON definition into a draft change set; with Dry-Run only the lint report.</summary>
    [HttpPost("import")]
    [Authorize(Policy = ProductPermissions.ProductImport)]
    public async Task<IResult> ImportAsync(
        [FromBody] ProductImportImportRequest request,
        [FromServices] ICommandHandler<ImportProductSource, ProductImportImportResponse> handler,
        CancellationToken cancellationToken) =>
        this.Respond(await handler.HandleAsync(new ImportProductSource(request), cancellationToken).ConfigureAwait(false));
}
