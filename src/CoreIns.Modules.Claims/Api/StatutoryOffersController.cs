using CoreIns.Modules.Claims.Commands.StatutoryOffers;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Http;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Claims.Api;

/// <summary>REST facade of <c>clm.StatutoryOffer.*</c> (BO-CLM-06): the statutory offer of a third-party MTPL exposure, three commands through the pipeline.</summary>
[ApiController]
[Route("api/clm/v1/statutory-offers")]
internal sealed class StatutoryOffersController : ControllerBase
{
    /// <summary>clm.StatutoryOffer.issue.</summary>
    [HttpPost("issue")]
    [Authorize(Policy = "clm.StatutoryOffer.issue")]
    public async Task<IResult> IssueAsync(
        [FromBody] StatutoryOfferIssueRequest request, [FromServices] ICommandHandler<IssueStatutoryOffer, StatutoryOfferIssueResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new IssueStatutoryOffer(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.StatutoryOffer.recordAcceptance.</summary>
    [HttpPost("record-acceptance")]
    [Authorize(Policy = "clm.StatutoryOffer.recordAcceptance")]
    public async Task<IResult> RecordAcceptanceAsync(
        [FromBody] StatutoryOfferRecordAcceptanceRequest request, [FromServices] ICommandHandler<RecordOfferAcceptance, StatutoryOfferRecordAcceptanceResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordOfferAcceptance(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.StatutoryOffer.recordRepairAgreement.</summary>
    [HttpPost("record-repair-agreement")]
    [Authorize(Policy = "clm.StatutoryOffer.recordRepairAgreement")]
    public async Task<IResult> RecordRepairAgreementAsync(
        [FromBody] StatutoryOfferRecordRepairAgreementRequest request, [FromServices] ICommandHandler<RecordRepairAgreement, StatutoryOfferRecordRepairAgreementResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordRepairAgreement(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
