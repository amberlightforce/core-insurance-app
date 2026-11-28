using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the disbursement batch operations (<c>x-permission</c>).</summary>
internal static class DisbursementBatchPermissions
{
    public const string Prepare = "bil.DisbursementBatch.prepare";
    public const string Release = "bil.DisbursementBatch.release";
    public const string ApproveRelease = "bil.DisbursementBatch.approveRelease";
}

/// <summary>
/// REST facade of the bank-file batches of non-claim disbursements (BO-BIL-10): prepare, request the release, and the second person's
/// approval of the release. The file (IBANs, payee names) is handed to the bank-file store and never returned.
/// </summary>
[ApiController]
[Route("api/bil/v1/disbursement-batches")]
internal sealed class DisbursementBatchController : ControllerBase
{
    /// <summary>bil.DisbursementBatch.prepare.</summary>
    [HttpPost("prepare")]
    [Authorize(Policy = DisbursementBatchPermissions.Prepare)]
    public async Task<IResult> PrepareAsync(
        [FromBody] DisbursementBatchPrepareRequest request, [FromServices] ICommandHandler<PrepareDisbursementBatch, DisbursementBatchPrepareResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PrepareDisbursementBatch(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.DisbursementBatch.release: the first person's request; rebuilds the file without stopped items.</summary>
    [HttpPost("release")]
    [Authorize(Policy = DisbursementBatchPermissions.Release)]
    public async Task<IResult> ReleaseAsync(
        [FromBody] DisbursementBatchReleaseRequest request, [FromServices] ICommandHandler<RequestDisbursementBatchRelease, DisbursementBatchReleaseResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RequestDisbursementBatchRelease(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.DisbursementBatch.approveRelease: the second person releases the batch to the bank.</summary>
    [HttpPost("approve-release")]
    [Authorize(Policy = DisbursementBatchPermissions.ApproveRelease)]
    public async Task<IResult> ApproveReleaseAsync(
        [FromBody] DisbursementBatchApproveReleaseRequest request, [FromServices] ICommandHandler<ApproveDisbursementBatchRelease, DisbursementBatchApproveReleaseResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveDisbursementBatchRelease(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
