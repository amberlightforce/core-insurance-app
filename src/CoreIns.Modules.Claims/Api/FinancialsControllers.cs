using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Queries;
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

namespace CoreIns.Modules.Claims.Api;

/// <summary>Permission names of the claim financial operations (x-permission), granted to the claims roles.</summary>
internal static class ClaimsFinancialPermissions
{
    public const string SetBuild = "clm.TransactionSet.build";
    public const string SetSubmit = "clm.TransactionSet.submit";
    public const string SetGet = "clm.TransactionSet.get";
    public const string FinancialsGet = "clm.Financials.get";
    public const string PaymentList = "clm.Payment.list";
    public const string PayeeCapture = "clm.PayeeAccount.capture";
    public const string PayeeList = "clm.PayeeAccount.list";
}

/// <summary>
/// REST facade of the claim financial engine (SL2-CLM-MONEY): transaction sets, balances, payments and payee accounts. A
/// referred set is decided in the PLT inbox (<c>plt.Approval.decide</c>); CLM applies the decision from <c>ApprovalDecided</c>.
/// </summary>
[ApiController]
[Route("api/clm/v1")]
internal sealed class FinancialsController : ControllerBase
{
    /// <summary>clm.TransactionSet.build (dry-run: preview only).</summary>
    [HttpPost("transaction-sets/build")]
    [Authorize(Policy = ClaimsFinancialPermissions.SetBuild)]
    public async Task<IResult> BuildAsync(
        [FromBody] TransactionSetBuildRequest request, [FromServices] ICommandHandler<BuildTransactionSet, TransactionSetBuildResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new BuildTransactionSet(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.TransactionSet.submit.</summary>
    [HttpPost("transaction-sets/submit")]
    [Authorize(Policy = ClaimsFinancialPermissions.SetSubmit)]
    public async Task<IResult> SubmitAsync(
        [FromBody] TransactionSetSubmitRequest request, [FromServices] ICommandHandler<SubmitTransactionSet, TransactionSetSubmitResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SubmitTransactionSet(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.TransactionSet.get.</summary>
    [HttpGet("transaction-sets/{id}")]
    [Authorize(Policy = ClaimsFinancialPermissions.SetGet)]
    public async Task<IResult> GetSetAsync(string id, [FromServices] FinancialsReader reader, CancellationToken cancellationToken)
    {
        var view = Guid.TryParse(id, out var setId)
            ? await reader.SetAsync(ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), new ClaimTransactionSetId(setId), cancellationToken).ConfigureAwait(false)
            : null;
        return view is null ? HttpResults.Problem(ClaimSupport.NotFound("transaction set"), HttpContext) : Results.Ok(new TransactionSetGetResponse { Set = view });
    }

    /// <summary>clm.Financials.get(claim, knownAt = asOf): derived balances as of a record time (REQ-CLM-101).</summary>
    [HttpGet("financials/get")]
    [Authorize(Policy = ClaimsFinancialPermissions.FinancialsGet)]
    public async Task<IResult> FinancialsAsync(
        [FromQuery] string? claim, [FromQuery] string? knownAt, [FromServices] FinancialsReader reader, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        Instant at = default;
        if (!Guid.TryParse(claim, out var claimId) || (knownAt is not null && !Instant.TryParse(knownAt, out at)))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.CLM, "VALIDATION", "claim must be a claim id and knownAt an RFC 3339 instant."), HttpContext);
        }

        var asOf = knownAt is null ? services.GetRequiredService<IClock>().Now : at;
        var id = new ClaimId(claimId);
        return await reader.ClaimExistsAsync(ClaimsQueryContext.LegalEntity(services), id, cancellationToken).ConfigureAwait(false)
            ? Results.Ok(await reader.GetAsync(id, asOf, cancellationToken).ConfigureAwait(false))
            : HttpResults.Problem(ClaimSupport.NotFound("claim"), HttpContext);
    }

    /// <summary>clm.Payment.list (the claim's payments; masked payee account).</summary>
    [HttpGet("claims/{id}/payments")]
    [Authorize(Policy = ClaimsFinancialPermissions.PaymentList)]
    public async Task<IResult> PaymentsAsync(string id, [FromServices] FinancialsReader reader, CancellationToken cancellationToken)
    {
        var claimId = await ExistingClaimAsync(id, reader, cancellationToken).ConfigureAwait(false);
        return claimId is null
            ? HttpResults.Problem(ClaimSupport.NotFound("claim"), HttpContext)
            : Results.Ok(new PaymentListPage { Items = await reader.PaymentsAsync(claimId.Value, cancellationToken).ConfigureAwait(false), NextCursor = null, Limit = 200 });
    }

    /// <summary>clm.PayeeAccount.capture → 201 (the IBAN goes to BIL only).</summary>
    [HttpPost("payee-accounts/capture")]
    [Authorize(Policy = ClaimsFinancialPermissions.PayeeCapture)]
    public async Task<IResult> CaptureAsync(
        [FromBody] PayeeAccountCaptureRequest request, [FromServices] ICommandHandler<CapturePayeeAccount, PayeeAccountCaptureResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CapturePayeeAccount(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/clm/v1/claims/{request.ClaimId.Value}/payee-accounts", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>clm.PayeeAccount.list (masked).</summary>
    [HttpGet("claims/{id}/payee-accounts")]
    [Authorize(Policy = ClaimsFinancialPermissions.PayeeList)]
    public async Task<IResult> PayeeAccountsAsync(string id, [FromServices] FinancialsReader reader, CancellationToken cancellationToken)
    {
        var claimId = await ExistingClaimAsync(id, reader, cancellationToken).ConfigureAwait(false);
        return claimId is null
            ? HttpResults.Problem(ClaimSupport.NotFound("claim"), HttpContext)
            : Results.Ok(new PayeeAccountListPage { Items = await reader.PayeeAccountsAsync(claimId.Value, cancellationToken).ConfigureAwait(false), NextCursor = null, Limit = 200 });
    }

    private async Task<ClaimId?> ExistingClaimAsync(string id, FinancialsReader reader, CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var guid) && await reader.ClaimExistsAsync(ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), new ClaimId(guid), cancellationToken).ConfigureAwait(false)
            ? new ClaimId(guid)
            : null;
}
