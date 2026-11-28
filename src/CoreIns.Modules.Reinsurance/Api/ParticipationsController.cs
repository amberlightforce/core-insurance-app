using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Reinsurance.Api;

/// <summary>Permission names of the panel, security rule and line table operations (BO-RI-05), granted in <c>permissions/ri.json</c>.</summary>
internal static class ParticipationPermissions
{
    public const string Add = "ri.Participation.add";
    public const string Replace = "ri.Participation.replace";
    public const string SecurityRulesSet = "ri.SecurityRules.set";
    public const string SecurityRulesGet = "ri.SecurityRules.get";
    public const string SecurityReport = "ri.Security.report";
    public const string LineTableSet = "ri.LineTable.set";
    public const string LineTableGet = "ri.LineTable.get";
}

/// <summary>
/// REST facade of the participations, security rules and line tables (BO-RI-05, contracts/openapi/ri.yaml). Thin; commands go
/// through the pipeline. A panel change is filed as an endorsement of the contract and decided under maker-checker.
/// </summary>
[ApiController]
[Route("api/ri/v1")]
internal sealed class ParticipationsController : ControllerBase
{
    /// <summary>ri.Participation.add: reinsurers added to the panel from an effective date (pending endorsement).</summary>
    [HttpPost("participations/add")]
    [Authorize(Policy = ParticipationPermissions.Add)]
    public async Task<IResult> AddAsync(
        [FromBody] ParticipationAddRequest request, [FromServices] ICommandHandler<AddParticipations, ParticipationAddResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AddParticipations(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Participation.replace: one reinsurer replaced by another from an effective date (pending endorsement).</summary>
    [HttpPost("participations/replace")]
    [Authorize(Policy = ParticipationPermissions.Replace)]
    public async Task<IResult> ReplaceAsync(
        [FromBody] ParticipationReplaceRequest request, [FromServices] ICommandHandler<ReplaceParticipation, ParticipationReplaceResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReplaceParticipation(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.SecurityRules.set: a new rule set from a date.</summary>
    [HttpPost("security-rules/set")]
    [Authorize(Policy = ParticipationPermissions.SecurityRulesSet)]
    public async Task<IResult> SetSecurityRulesAsync(
        [FromBody] SecurityRulesSetRequest request, [FromServices] ICommandHandler<SetSecurityRules, SecurityRulesSetResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SetSecurityRules(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.SecurityRules.get: a rule set by id (<c>current</c> is the one in force today).</summary>
    [HttpGet("security-rules/{id}")]
    [Authorize(Policy = ParticipationPermissions.SecurityRulesGet)]
    public async Task<IResult> GetSecurityRulesAsync(string id, [FromServices] SecurityReader reader, CancellationToken cancellationToken) =>
        (await reader.GetAsync(id, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.Security.report: exposure per reinsurer at <c>validAt</c> (a date, default today), judged against the rules in force then or the rule set named.</summary>
    [HttpPost("securities/report")]
    [Authorize(Policy = ParticipationPermissions.SecurityReport)]
    public async Task<IResult> ReportAsync(
        [FromQuery] string? validAt, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] SecurityReportRequest? request,
        [FromServices] SecurityReader reader, [FromServices] Platform.Time.IClock clock, [FromServices] Microsoft.Extensions.Options.IOptions<ReinsuranceOptions> options,
        CancellationToken cancellationToken)
    {
        BusinessDate date;
        if (string.IsNullOrWhiteSpace(validAt))
        {
            date = clock.Now.ToBusinessDate(options.Value.Zone);
        }
        else if (BusinessDate.TryParse(validAt, out var parsed))
        {
            date = parsed;
        }
        else if (Instant.TryParse(validAt, out var instant))
        {
            date = instant.ToBusinessDate(options.Value.Zone);
        }
        else
        {
            return RiHttp.Problem(RiErrors.Validation("validAt", "VALID_AT", "validAt must be a date or an instant."), HttpContext);
        }

        SecurityRuleSet? rules;
        if (request?.RuleSetId is { } ruleSetId)
        {
            rules = await reader.FindAsync(ruleSetId, cancellationToken).ConfigureAwait(false);
            if (rules is null)
            {
                return RiHttp.Problem(RiErrors.Validation("ruleSetId", "RULE_SET_NOT_FOUND", "The rule set does not exist."), HttpContext);
            }
        }
        else
        {
            rules = await reader.InForceAsync(date, cancellationToken).ConfigureAwait(false);
        }

        return Results.Ok(await reader.ReportAsync(date, rules, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>ri.LineTable.set: a new version of the line table.</summary>
    [HttpPost("line-tables/set")]
    [Authorize(Policy = ParticipationPermissions.LineTableSet)]
    public async Task<IResult> SetLineTableAsync(
        [FromBody] LineTableSetRequest request, [FromServices] ICommandHandler<SetLineTable, LineTableSetResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SetLineTable(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>ri.LineTable.get: a version by id (<c>current</c> is the latest).</summary>
    [HttpGet("line-tables/{id}")]
    [Authorize(Policy = ParticipationPermissions.LineTableGet)]
    public async Task<IResult> GetLineTableAsync(string id, [FromServices] LineTableReader reader, CancellationToken cancellationToken) =>
        (await reader.GetAsync(id, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
