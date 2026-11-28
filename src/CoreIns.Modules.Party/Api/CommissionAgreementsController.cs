using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Party.Api;

/// <summary>REST facade of <c>pty.CommissionAgreement.*</c> (BO-PTY-10, REQ-PTY-010). resolve and simulate: BO-PTY-11.</summary>
[ApiController]
[Route("api/pty/v1/commission-agreements")]
internal sealed class CommissionAgreementsController : ControllerBase
{
    /// <summary>pty.CommissionAgreement.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = "pty.CommissionAgreement.get")]
    public async Task<IResult> GetAsync(
        string id, [FromServices] CommissionAgreementQueries queries, [FromServices] PartyProtection protection, [FromServices] RequestContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return HttpResults.Problem(DomainError.Of(ModuleCode.PTY, "VALIDATION", "The agreement id is not a valid identifier."), HttpContext);
        }

        var agreement = await queries.AgreementAsync(protection.Current(context), guid, cancellationToken).ConfigureAwait(false);
        return agreement is null
            ? HttpResults.Problem(CommissionSupport.NotFound(), HttpContext)
            : Results.Ok(new CommissionAgreementGetResponse { Agreement = agreement });
    }

    /// <summary>pty.CommissionAgreement.createVersion → 201.</summary>
    [HttpPost("create-version")]
    [Authorize(Policy = "pty.CommissionAgreement.createVersion")]
    public async Task<IResult> CreateVersionAsync(
        [FromBody] CommissionAgreementCreateVersionRequest request, [FromServices] ICommandHandler<CreateCommissionVersion, CommissionAgreementCreateVersionResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateCommissionVersion(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pty/v1/commission-agreements/{result.Value.Agreement.AgreementId}", result.Value)
            : HttpResults.Problem(result.Error, HttpContext);
    }

    /// <summary>pty.CommissionAgreement.submit.</summary>
    [HttpPost("submit")]
    [Authorize(Policy = "pty.CommissionAgreement.submit")]
    public async Task<IResult> SubmitAsync(
        [FromBody] CommissionAgreementSubmitRequest request, [FromServices] ICommandHandler<SubmitCommissionVersion, CommissionAgreementSubmitResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SubmitCommissionVersion(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.CommissionAgreement.approve.</summary>
    [HttpPost("approve")]
    [Authorize(Policy = "pty.CommissionAgreement.approve")]
    public async Task<IResult> ApproveAsync(
        [FromBody] CommissionAgreementApproveRequest request, [FromServices] ICommandHandler<ApproveCommissionVersion, CommissionAgreementApproveResponse> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ApproveCommissionVersion(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.CommissionAgreement.compare: two versions of one agreement.</summary>
    [HttpGet("compare")]
    [Authorize(Policy = "pty.CommissionAgreement.compare")]
    public async Task<IResult> CompareAsync(
        [FromQuery] Guid fromVersionId, [FromQuery] Guid toVersionId, [FromServices] CommissionAgreementQueries queries,
        [FromServices] PartyProtection protection, [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        var comparison = await queries.CompareAsync(protection.Current(context), fromVersionId, toVersionId, cancellationToken).ConfigureAwait(false);
        return comparison is null
            ? HttpResults.Problem(CommissionSupport.NotFound(), HttpContext)
            : Results.Ok(comparison);
    }

    /// <summary>pty.CommissionAgreement.resolve (BO-PTY-11): the producers, agreement version and rate line that apply, with the reason. Read only.</summary>
    [HttpPost("resolve")]
    [Authorize(Policy = "pty.CommissionAgreement.resolve")]
    public async Task<IResult> ResolveAsync(
        [FromBody] CommissionAgreementResolveRequest request, [FromServices] CommissionResolution resolution, CancellationToken cancellationToken) =>
        (await resolution.ResolveAsync(request, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pty.CommissionAgreement.simulate (BO-PTY-11): commission on premium lines, nothing written.</summary>
    [HttpPost("simulate")]
    [Authorize(Policy = "pty.CommissionAgreement.simulate")]
    public async Task<IResult> SimulateAsync(
        [FromBody] CommissionAgreementSimulateRequest request, [FromServices] CommissionResolution resolution, CancellationToken cancellationToken) =>
        (await resolution.SimulateAsync(request, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
