using System.Text.Json;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Claims.Api;

/// <summary>REST facade of <c>clm.Claim.reopen|merge</c> and <c>clm.Fnol.saveDraft|getDraft</c> (BO-CLM-02).</summary>
[ApiController]
[Route("api/clm/v1")]
internal sealed class ClaimLifecycleController : ControllerBase
{
    /// <summary>clm.Claim.reopen.</summary>
    [HttpPost("claims/reopen")]
    [Authorize(Policy = ClaimsPermissions.ClaimReopen)]
    public async Task<IResult> ReopenAsync(
        [FromBody] ClaimReopenRequest request, [FromServices] ICommandHandler<ReopenClaim, ClaimReopenResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReopenClaim(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Claim.merge; CLM-ERR-MERGE-FINANCIALS carries <c>errors[]</c> { code, exposureIds[] } like the close guard.</summary>
    [HttpPost("claims/merge")]
    [Authorize(Policy = ClaimsPermissions.ClaimMerge)]
    public async Task<IResult> MergeAsync(
        [FromBody] ClaimMergeRequest request, [FromServices] ICommandHandler<MergeClaims, ClaimMergeResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new MergeClaims(request), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure && result.Error!.Code.Value == "CLM-ERR-MERGE-FINANCIALS"
            && result.Error.Metadata.TryGetValue("closeGuardErrors", out var json))
        {
            var problem = HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().Create(result.Error, HttpContext);
            problem.Extensions.Remove("closeGuardErrors");
            problem.Extensions["errors"] = JsonSerializer.Deserialize<CloseGuardError[]>(json, CoreIns.SharedKernel.Json.SharedKernelJson.Options);
            return Results.Problem(problem);
        }

        return result.ToHttpResult(HttpContext);
    }

    /// <summary>clm.Fnol.saveDraft: no claim number, no events.</summary>
    [HttpPost("fnol/save-draft")]
    [Authorize(Policy = ClaimsPermissions.FnolSaveDraft)]
    public async Task<IResult> SaveDraftAsync(
        [FromBody] FnolSaveDraftRequest request, [FromServices] ICommandHandler<SaveFnolDraft, FnolSaveDraftResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SaveFnolDraft(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Fnol.getDraft: the creator's own draft only.</summary>
    [HttpGet("fnol/drafts/{draftId}")]
    [Authorize(Policy = ClaimsPermissions.FnolGetDraft)]
    public async Task<IResult> GetDraftAsync(string draftId, [FromServices] FnolDraftReader reader, CancellationToken cancellationToken)
    {
        var view = Guid.TryParse(draftId, out var guid) ? await reader.GetAsync(guid, cancellationToken).ConfigureAwait(false) : null;
        return view is null ? HttpResults.Problem(ClaimSupport.NotFound("FNOL draft"), HttpContext) : Results.Ok(view);
    }
}
