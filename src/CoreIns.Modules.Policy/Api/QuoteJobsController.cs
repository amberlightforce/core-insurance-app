using CoreIns.Modules.Policy.Commands.Quotes;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Policy.Api;

/// <summary>REST facade of the quote job lifecycle (BO-POL-03): <c>pol.Job.list</c>, <c>newVersion</c>, <c>requote</c>, <c>copyVersion</c>, <c>notTaken</c>. Thin: the commands run through the pipeline.</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class QuoteJobsController : ControllerBase
{
    /// <summary>pol.Job.list: filters by state, party, producer code, product, job type, account, policy, participant; keyset-paged.</summary>
    [HttpGet("jobs")]
    [Authorize(Policy = "pol.Job.list")]
    public async Task<IResult> ListAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? account, [FromQuery] string? policy, [FromQuery] string? participant,
        [FromQuery] string? state, [FromQuery] string? partyId, [FromQuery] string? producerCode, [FromQuery] string? productCode, [FromQuery] string? jobType,
        [FromServices] JobList list, CancellationToken cancellationToken) =>
        (await list.ListAsync(new JobListFilter(account, policy, participant, state, partyId, producerCode, productCode, jobType), cursor, limit, cancellationToken)
            .ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Job.newVersion.</summary>
    [HttpPost("jobs/new-version")]
    [Authorize(Policy = "pol.Job.newVersion")]
    public async Task<IResult> NewVersionAsync(
        [FromBody] JobNewVersionRequest request, [FromServices] ICommandHandler<NewVersion, JobNewVersionResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new NewVersion(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Job.copyVersion.</summary>
    [HttpPost("jobs/copy-version")]
    [Authorize(Policy = "pol.Job.copyVersion")]
    public async Task<IResult> CopyVersionAsync(
        [FromBody] JobCopyVersionRequest request, [FromServices] ICommandHandler<CopyVersion, JobCopyVersionResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CopyVersion(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Job.requote: with <c>?dryRun=true</c> the full pipeline runs and nothing is kept.</summary>
    [HttpPost("jobs/requote")]
    [Authorize(Policy = "pol.Job.requote")]
    public async Task<IResult> RequoteAsync(
        [FromBody] JobRequoteRequest request, [FromServices] ICommandHandler<Requote, JobRequoteResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new Requote(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Job.notTaken.</summary>
    [HttpPost("jobs/not-taken")]
    [Authorize(Policy = "pol.Job.notTaken")]
    public async Task<IResult> NotTakenAsync(
        [FromBody] JobNotTakenRequest request, [FromServices] ICommandHandler<NotTaken, JobNotTakenResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new NotTaken(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
