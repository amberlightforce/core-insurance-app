using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.MigrationImport;
using CoreIns.Modules.Claims.Services.Dsar;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Claims.Api;

/// <summary>
/// REST facade of <c>clm.Import.*</c> (migration service actor: Staff.MigrationManager, Platform.Admin) and <c>clm.Dsar.*</c> (CMP fan-out
/// and the DPO): thin, commands through the pipeline (BO-CLM-14).
/// </summary>
[ApiController]
[Route("api/clm/v1")]
internal sealed class ClaimsImportController : ControllerBase
{
    /// <summary>clm.Import.claim (dry-run supported).</summary>
    [HttpPost("import/claim")]
    [Authorize(Policy = "clm.Import.claim")]
    public async Task<IResult> ImportClaimAsync([FromBody] ImportClaimRequest request, [FromServices] ICommandHandler<ImportClaim, ImportClaimResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ImportClaim(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Import.financialHistory.</summary>
    [HttpPost("import/financial-history")]
    [Authorize(Policy = "clm.Import.financialHistory")]
    public async Task<IResult> ImportHistoryAsync(
        [FromBody] ImportFinancialHistoryRequest request, [FromServices] ICommandHandler<ImportFinancialHistory, ImportFinancialHistoryResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ImportFinancialHistory(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Import.convertCurrency.</summary>
    [HttpPost("import/convert-currency")]
    [Authorize(Policy = "clm.Import.convertCurrency")]
    public async Task<IResult> ConvertCurrencyAsync(
        [FromBody] ImportConvertCurrencyRequest request, [FromServices] ICommandHandler<ImportConvertCurrency, ImportConvertCurrencyResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ImportConvertCurrency(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Import.reverse.</summary>
    [HttpPost("import/reverse")]
    [Authorize(Policy = "clm.Import.reverse")]
    public async Task<IResult> ReverseAsync([FromBody] ImportReverseRequest request, [FromServices] ICommandHandler<ImportReverse, ImportReverseResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ImportReverse(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Dsar.export.</summary>
    [HttpPost("dsar/export")]
    [Authorize(Policy = "clm.Dsar.export")]
    public async Task<IResult> ExportAsync([FromBody] DsarExportRequest request, [FromServices] ICommandHandler<DsarExport, DsarExportResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DsarExport(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Dsar.rectify.</summary>
    [HttpPost("dsar/rectify")]
    [Authorize(Policy = "clm.Dsar.rectify")]
    public async Task<IResult> RectifyAsync([FromBody] DsarRectifyRequest request, [FromServices] ICommandHandler<DsarRectify, DsarRectifyResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DsarRectify(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Dsar.restrict.</summary>
    [HttpPost("dsar/restrict")]
    [Authorize(Policy = "clm.Dsar.restrict")]
    public async Task<IResult> RestrictAsync([FromBody] DsarRestrictRequest request, [FromServices] ICommandHandler<DsarRestrict, DsarRestrictResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DsarRestrict(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Dsar.purge.</summary>
    [HttpPost("dsar/purge")]
    [Authorize(Policy = "clm.Dsar.purge")]
    public async Task<IResult> PurgeAsync([FromBody] DsarPurgeRequest request, [FromServices] ICommandHandler<DsarPurge, DsarPurgeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DsarPurge(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
