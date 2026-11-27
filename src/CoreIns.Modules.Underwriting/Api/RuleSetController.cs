using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Underwriting.Api;

/// <summary>Permissions of the rule-set authoring operations (the contract's <c>x-permission</c>).</summary>
internal static class RuleSetPermissions
{
    public const string Create = "uw.RuleSet.create";
    public const string Draft = "uw.RuleSet.draft";
    public const string Edit = "uw.RuleSet.edit";
    public const string Validate = "uw.RuleSet.validate";
    public const string Test = "uw.RuleSet.test";
    public const string Simulate = "uw.RuleSet.simulate";
    public const string Export = "uw.RuleSet.export";
    public const string Import = "uw.RuleSet.import";
    public const string Submit = "uw.RuleSet.submit";
    public const string Approve = "uw.RuleSet.approve";
    public const string Schedule = "uw.RuleSet.schedule";
}

/// <summary>REST facade of the rule-set authoring operations (BO-UW-01).</summary>
[ApiController]
[Route("api/uw/v1/rule-sets")]
internal sealed class RuleSetController : ControllerBase
{
    /// <summary>uw.RuleSet.create: a new rule set with a first Draft version.</summary>
    [HttpPost]
    [Authorize(Policy = RuleSetPermissions.Create)]
    public async Task<IResult> CreateAsync(
        [FromBody] RuleSetCreateRequest request, [FromServices] ICommandHandler<CreateRuleSet, RuleSetAuthoringResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateRuleSet(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.draft: a new Draft version from an existing version.</summary>
    [HttpPost("draft")]
    [Authorize(Policy = RuleSetPermissions.Draft)]
    public async Task<IResult> DraftAsync(
        [FromBody] RuleSetDraftRequest request, [FromServices] ICommandHandler<DraftRuleSet, RuleSetAuthoringResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DraftRuleSet(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.edit: patches to a Draft version.</summary>
    [HttpPost("edit")]
    [Authorize(Policy = RuleSetPermissions.Edit)]
    public async Task<IResult> EditAsync(
        [FromBody] RuleSetEditRequest request, [FromServices] ICommandHandler<EditRuleSet, RuleSetAuthoringResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new EditRuleSet(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.import: a Draft version from an exported document.</summary>
    [HttpPost("import")]
    [Authorize(Policy = RuleSetPermissions.Import)]
    public async Task<IResult> ImportAsync(
        [FromBody] RuleSetImportRequest request, [FromServices] ICommandHandler<ImportRuleSet, RuleSetAuthoringResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ImportRuleSet(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.submit: a valid Draft goes to maker-checker approval.</summary>
    [HttpPost("submit")]
    [Authorize(Policy = RuleSetPermissions.Submit)]
    public async Task<IResult> SubmitAsync(
        [FromBody] RuleSetSubmitRequest request, [FromServices] ICommandHandler<SubmitRuleSet, RuleSetReleaseResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new SubmitRuleSet(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.approve: the checker approves (or rejects) a Submitted version.</summary>
    [HttpPost("approve")]
    [Authorize(Policy = RuleSetPermissions.Approve)]
    public async Task<IResult> ApproveAsync(
        [FromBody] RuleSetApproveRequest request, [FromServices] ICommandHandler<ApproveRuleSet, RuleSetReleaseResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ApproveRuleSet(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.schedule: an Approved version becomes Active from a date (never in the past).</summary>
    [HttpPost("schedule")]
    [Authorize(Policy = RuleSetPermissions.Schedule)]
    public async Task<IResult> ScheduleAsync(
        [FromBody] RuleSetScheduleRequest request, [FromServices] ICommandHandler<ScheduleRuleSet, RuleSetReleaseResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ScheduleRuleSet(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.validate: every finding of a version (read only, a POST only for its body).</summary>
    [HttpPost("validate")]
    [SkipIdempotency]
    [Authorize(Policy = RuleSetPermissions.Validate)]
    public async Task<IResult> ValidateAsync([FromBody] RuleSetRef request, [FromServices] RuleSetAuthoringQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.test: runs the test cases of a version (read only).</summary>
    [HttpPost("test")]
    [SkipIdempotency]
    [Authorize(Policy = RuleSetPermissions.Test)]
    public async Task<IResult> TestAsync([FromBody] RuleSetRef request, [FromServices] RuleSetAuthoringQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.simulate: a version against stored evaluations, hit changes only (read only).</summary>
    [HttpPost("simulate")]
    [SkipIdempotency]
    [Authorize(Policy = RuleSetPermissions.Simulate)]
    public async Task<IResult> SimulateAsync([FromBody] RuleSetSimulateRequest request, [FromServices] RuleSetAuthoringQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.SimulateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    /// <summary>uw.RuleSet.export: the portable JSON document of a version (read only).</summary>
    [HttpPost("export")]
    [SkipIdempotency]
    [Authorize(Policy = RuleSetPermissions.Export)]
    public async Task<IResult> ExportAsync([FromBody] RuleSetRef request, [FromServices] RuleSetAuthoringQueries queries, CancellationToken cancellationToken)
    {
        var result = await queries.ExportAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error, HttpContext);
    }

    private static IResult Problem(DomainError error, HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, http);
}
