using System.Text.Json;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Claims.Api;

/// <summary>Permission names (the operations' <c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
internal static class ClaimsPermissions
{
    public const string FnolSubmit = "clm.Fnol.submit";
    public const string FnolGet = "clm.Fnol.get";
    public const string FnolValidate = "clm.Fnol.validate";
    public const string ClaimGet = "clm.Claim.get";
    public const string ClaimSearch = "clm.Claim.search";
    public const string ClaimClose = "clm.Claim.close";
    public const string ExposureCreate = "clm.Exposure.create";
    public const string CoverageReverify = "clm.Coverage.reverify";
}

/// <summary>REST facade of <c>clm.Fnol.*</c> (contracts/openapi/clm.yaml): thin; commands through the pipeline.</summary>
[ApiController]
[Route("api/clm/v1/fnol")]
internal sealed class FnolController : ControllerBase
{
    /// <summary>clm.Fnol.submit (staff channel; dry-run per contract §3.5.3).</summary>
    [HttpPost("submit")]
    [Authorize(Policy = ClaimsPermissions.FnolSubmit)]
    public async Task<IResult> SubmitAsync(
        [FromBody] FnolSubmitRequest request, [FromServices] ICommandHandler<SubmitFnol, FnolSubmitResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SubmitFnol(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Fnol.validate: a read (no Idempotency-Key, nothing stored).</summary>
    [HttpPost("validate")]
    [SkipIdempotency]
    [Authorize(Policy = ClaimsPermissions.FnolValidate)]
    public async Task<IResult> ValidateAsync([FromBody] FnolValidateRequest request, CancellationToken cancellationToken) =>
        Results.Ok(await HttpContext.RequestServices.GetRequiredService<FnolValidation>().ValidateAsync(request, cancellationToken).ConfigureAwait(false));

    /// <summary>clm.Fnol.get by FNOL id or claim id (REQ-CLM-044).</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = ClaimsPermissions.FnolGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] ClaimReader reader, CancellationToken cancellationToken)
    {
        var view = Guid.TryParse(id, out var guid)
            ? await reader.GetFnolAsync(ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), guid, cancellationToken).ConfigureAwait(false)
            : null;
        return view is null ? HttpResults.Problem(ClaimSupport.NotFound("FNOL"), HttpContext) : Results.Ok(view);
    }
}

/// <summary>REST facade of <c>clm.Claim.*</c> and <c>clm.Exposure.create</c>.</summary>
[ApiController]
[Route("api/clm/v1")]
internal sealed class ClaimsController : ControllerBase
{
    /// <summary>clm.Claim.search (GET form): claim number only; every other criterion goes in the POST body (D-SLC-05).</summary>
    [HttpGet("claims/search")]
    [Authorize(Policy = ClaimsPermissions.ClaimSearch)]
    public Task<IResult> SearchAsync(
        [FromQuery] string? claimNumber, [FromQuery] int? limit, [FromQuery] string? cursor, [FromServices] ClaimReader reader, CancellationToken cancellationToken) =>
        RunSearchAsync(reader, new ClaimSearchCriteria { ClaimNumber = claimNumber }, limit, cursor, cancellationToken);

    /// <summary>clm.Claim.searchByCriteria (POST): policy number and party ids travel in the body, never in a URL (D-SLC-05).</summary>
    [HttpPost("claims/search")]
    [SkipIdempotency]
    [Authorize(Policy = ClaimsPermissions.ClaimSearch)]
    public Task<IResult> SearchByCriteriaAsync(
        [FromBody] ClaimSearchCriteria criteria, [FromQuery] int? limit, [FromQuery] string? cursor, [FromServices] ClaimReader reader,
        CancellationToken cancellationToken) =>
        RunSearchAsync(reader, criteria, limit, cursor, cancellationToken);

    /// <summary>clm.Claim.get (REQ-CLM-061).</summary>
    [HttpGet("claims/{id}")]
    [Authorize(Policy = ClaimsPermissions.ClaimGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] ClaimReader reader, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        var view = Guid.TryParse(id, out var claimId)
            ? await reader.GetAsync(ClaimsQueryContext.LegalEntity(services), ClaimsQueryContext.LegalEntityCode(services), new ClaimId(claimId), cancellationToken)
                .ConfigureAwait(false)
            : null;
        return view is null ? HttpResults.Problem(ClaimSupport.NotFound("claim"), HttpContext) : Results.Ok(new ClaimGetResponse { Claim = view });
    }

    /// <summary>clm.Claim.close (REQ-CLM-071..073).</summary>
    [HttpPost("claims/close")]
    [Authorize(Policy = ClaimsPermissions.ClaimClose)]
    public async Task<IResult> CloseAsync(
        [FromBody] ClaimCloseRequest request, [FromServices] ICommandHandler<CloseClaim, ClaimCloseResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CloseClaim(request), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure && result.Error!.Code.Value == "CLM-ERR-CLOSE-GUARD"
            && result.Error.Metadata.TryGetValue("closeGuardErrors", out var json))
        {
            var problem = HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().Create(result.Error, HttpContext);
            problem.Extensions.Remove("closeGuardErrors");
            problem.Extensions["errors"] = JsonSerializer.Deserialize<CloseGuardError[]>(json, CoreIns.SharedKernel.Json.SharedKernelJson.Options);
            return Results.Problem(problem);
        }

        return result.ToHttpResult(HttpContext);
    }

    /// <summary>clm.Exposure.create → 201 (REQ-CLM-062, -063).</summary>
    [HttpPost("exposures")]
    [Authorize(Policy = ClaimsPermissions.ExposureCreate)]
    public async Task<IResult> CreateExposureAsync(
        [FromBody] ExposureCreateRequest request, [FromServices] ICommandHandler<CreateExposure, ExposureCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateExposure(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/clm/v1/claims/{result.Value.Claim.ClaimId.Value}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>clm.Coverage.reverify: keep or adopt the superseding POL snapshot (REQ-CLM-058).</summary>
    [HttpPost("coverage/reverify")]
    [Authorize(Policy = ClaimsPermissions.CoverageReverify)]
    public async Task<IResult> ReverifyAsync(
        [FromBody] CoverageReverifyRequest request, [FromServices] ICommandHandler<ReverifyCoverage, CoverageReverifyResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReverifyCoverage(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private async Task<IResult> RunSearchAsync(ClaimReader reader, ClaimSearchCriteria criteria, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var result = await ClaimSearch.RunAsync(reader, ClaimsQueryContext.LegalEntity(HttpContext.RequestServices), criteria, limit, cursor, cancellationToken)
            .ConfigureAwait(false);
        return result.ToHttpResult(HttpContext);
    }
}

/// <summary>Search criteria rules shared by the REST facade and the in-process contract (REQ-CLM-011 subset).</summary>
internal static class ClaimSearch
{
    public static async Task<Result<ClaimSearchPage>> RunAsync(
        ClaimReader reader, LegalEntityId legalEntity, ClaimSearchCriteria criteria, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var claimNumber = Clean(criteria.ClaimNumber);
        var policyNumber = Clean(criteria.PolicyNumber);
        if (claimNumber is null && policyNumber is null && criteria.InsuredPartyId is null)
        {
            return DomainError.Of(ModuleCode.CLM, "SEARCH-CRITERIA", "Give a claim number, a policy number or an insured party.");
        }

        if (!ClaimReader.TryPage(limit, out var take) || cursor is { Length: > 64 })
        {
            return DomainError.Of(ModuleCode.CLM, "SEARCH-CRITERIA", "limit must be 1..200 and cursor a claim number.");
        }

        return await reader.SearchAsync(legalEntity, claimNumber, policyNumber, criteria.InsuredPartyId?.Value, cursor, take, cancellationToken).ConfigureAwait(false);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The caller's legal entity for queries.</summary>
internal static class ClaimsQueryContext
{
    public static LegalEntityId LegalEntity(IServiceProvider services) =>
        services.GetRequiredService<ClaimProtection>().Current(services.GetRequiredService<RequestContext>());

    public static string LegalEntityCode(IServiceProvider services) => services.GetRequiredService<RequestContext>().LegalEntity!.Value.Value;
}

/// <summary>Problem Details from a domain error.</summary>
internal static class HttpResults
{
    public static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
