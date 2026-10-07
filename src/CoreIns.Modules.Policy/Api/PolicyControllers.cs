using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Policy.Api;

/// <summary>Permission names (the operations' <c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
internal static class PolicyPermissions
{
    public const string SubmissionCreate = "pol.Submission.create";
    public const string JobUpdateDraft = "pol.Job.updateDraft";
    public const string JobQuote = "pol.Job.quote";
    public const string JobBind = "pol.Job.bind";
    public const string JobGet = "pol.Job.get";
    public const string PolicyGet = "pol.Policy.get";
    public const string TermGet = "pol.Term.get";
}

/// <summary>REST facade of <c>pol.Submission.*</c> and <c>pol.Job.*</c> (contracts/openapi/pol.yaml): thin, commands through the pipeline.</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class JobsController : ControllerBase
{
    /// <summary>pol.Submission.create → 201, Location = the job.</summary>
    [HttpPost("submissions")]
    [Authorize(Policy = PolicyPermissions.SubmissionCreate)]
    public async Task<IResult> CreateSubmissionAsync(
        [FromBody] SubmissionCreateRequest request, [FromServices] ICommandHandler<CreateSubmission, SubmissionCreateResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateSubmission(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/pol/v1/jobs/{result.Value.JobId.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>pol.Job.updateDraft.</summary>
    [HttpPost("jobs/update-draft")]
    [Authorize(Policy = PolicyPermissions.JobUpdateDraft)]
    public async Task<IResult> UpdateDraftAsync(
        [FromBody] JobUpdateDraftRequest request, [FromServices] ICommandHandler<UpdateDraft, JobUpdateDraftResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new UpdateDraft(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Job.quote.</summary>
    [HttpPost("jobs/quote")]
    [Authorize(Policy = PolicyPermissions.JobQuote)]
    public async Task<IResult> QuoteAsync(
        [FromBody] JobQuoteRequest request, [FromServices] ICommandHandler<QuoteJob, JobQuoteResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new QuoteJob(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Job.bind.</summary>
    [HttpPost("jobs/bind")]
    [Authorize(Policy = PolicyPermissions.JobBind)]
    public async Task<IResult> BindAsync(
        [FromBody] JobBindRequest request, [FromServices] ICommandHandler<BindJob, JobBindResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new BindJob(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.Job.get.</summary>
    [HttpGet("jobs/{id}")]
    [Authorize(Policy = PolicyPermissions.JobGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] JobReader reader, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        var view = Guid.TryParse(id, out var jobId)
            ? await reader.GetAsync(PolicyQueryContext.LegalEntity(services), jobId, cancellationToken).ConfigureAwait(false)
            : null;
        return view is null ? HttpResults.Problem(JobSupport.NotFound("job"), HttpContext) : Results.Ok(new JobGetResponse { Job = view });
    }
}

/// <summary>REST facade of <c>pol.Policy.get</c> and <c>pol.Term.get</c>: bitemporal reads by <c>validAt</c> / <c>knownAt</c> (D-API-08).</summary>
[ApiController]
[Route("api/pol/v1")]
internal sealed class PoliciesController : ControllerBase
{
    /// <summary>pol.Policy.get as of validAt (date or instant, default now) and knownAt (instant, default now).</summary>
    [HttpGet("policies/{id}")]
    [Authorize(Policy = PolicyPermissions.PolicyGet)]
    public async Task<IResult> GetPolicyAsync(
        string id, [FromQuery] string? validAt, [FromQuery] string? knownAt, [FromServices] PolicyReader reader, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        if (!PolicyQueryContext.TryTime(services, validAt, knownAt, out var valid, out var known))
        {
            return HttpResults.Problem(PolicyQueryContext.BadTime(), HttpContext);
        }

        var response = Guid.TryParse(id, out var policyId)
            ? await reader.GetPolicyAsync(PolicyQueryContext.LegalEntity(services), PolicyQueryContext.LegalEntityCode(services), policyId, valid, known, cancellationToken)
                .ConfigureAwait(false)
            : null;
        return response is null ? HttpResults.Problem(JobSupport.NotFound("policy"), HttpContext) : Results.Ok(response);
    }

    /// <summary>pol.Term.get as of validAt / knownAt.</summary>
    [HttpGet("terms/{id}")]
    [Authorize(Policy = PolicyPermissions.TermGet)]
    public async Task<IResult> GetTermAsync(
        string id, [FromQuery] string? validAt, [FromQuery] string? knownAt, [FromServices] PolicyReader reader, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        if (!PolicyQueryContext.TryTime(services, validAt, knownAt, out var valid, out var known))
        {
            return HttpResults.Problem(PolicyQueryContext.BadTime(), HttpContext);
        }

        var response = Guid.TryParse(id, out var termId)
            ? await reader.GetTermAsync(PolicyQueryContext.LegalEntity(services), PolicyQueryContext.LegalEntityCode(services), termId, valid, known, cancellationToken)
                .ConfigureAwait(false)
            : null;
        return response is null ? HttpResults.Problem(JobSupport.NotFound("term"), HttpContext) : Results.Ok(response);
    }
}

/// <summary>Request context helpers of the read side.</summary>
internal static class PolicyQueryContext
{
    public static LegalEntityId LegalEntity(IServiceProvider services) =>
        JobSupport.LegalEntity(services.GetRequiredService<RequestContext>(), services.GetRequiredService<ILegalEntityDirectory>());

    public static string LegalEntityCode(IServiceProvider services) => services.GetRequiredService<RequestContext>().LegalEntity!.Value.Value;

    /// <summary>
    /// Parses <c>validAt</c> (an instant as given, or a date meaning the end of that business day in the legal entity's zone, D-SLC-13) and
    /// <c>knownAt</c> (an instant); both default to now.
    /// </summary>
    public static bool TryTime(IServiceProvider services, string? validAt, string? knownAt, out Instant valid, out Instant known)
    {
        var now = services.GetRequiredService<IClock>().Now;
        valid = now;
        known = now;
        if (validAt is not null)
        {
            if (Instant.TryParse(validAt, out var instant))
            {
                valid = instant;
            }
            else if (BusinessDate.TryParse(validAt, out var date))
            {
                valid = PolicyTime.EndOf(date, services.GetRequiredService<IOptions<PolicyOptions>>().Value.Zone);
            }
            else
            {
                return false;
            }
        }

        return knownAt is null || Instant.TryParse(knownAt, out known);
    }

    public static DomainError BadTime() => DomainError.Of(ModuleCode.POL, "VALIDATION", "validAt must be a date or instant, knownAt an instant.");
}

/// <summary>Problem Details from a domain error.</summary>
internal static class HttpResults
{
    public static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
