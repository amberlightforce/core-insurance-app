using CoreIns.Modules.Finance.Commands;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Finance.Api;

/// <summary>Permissions of the posting-rule authoring operations (the operations' <c>x-permission</c>).</summary>
internal static class PostingRuleAuthoringPermissions
{
    public const string Draft = "fin.PostingRules.draft";
    public const string Compile = "fin.PostingRules.compile";
    public const string Test = "fin.PostingRules.test";
    public const string PreviewImpact = "fin.PostingRules.previewImpact";
    public const string Submit = "fin.PostingRules.submit";
    public const string Decide = "fin.PostingRules.decide";
}

/// <summary>
/// REST facade of posting-rule authoring (BO-FIN-07): draft a rule-set version, compile, test against sample events, preview the impact on
/// recent postings (both read-only), and submit for a maker-checker decision. An approved set takes effect from its effective date only.
/// </summary>
[ApiController]
[Route("api/fin/v1/posting-rules")]
internal sealed class PostingRuleAuthoringController : ControllerBase
{
    /// <summary>fin.PostingRules.draft: store or replace a DRAFT rule-set version.</summary>
    [HttpPost("draft")]
    [Authorize(Policy = PostingRuleAuthoringPermissions.Draft)]
    public async Task<IResult> DraftAsync([FromBody] PostingRulesAuthoringInput request, [FromServices] ICommandHandler<DraftPostingRules, PostingRulesAuthoringResult> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DraftPostingRules(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>fin.PostingRules.compile: compile checks of a draft or an inline rule set.</summary>
    [HttpPost("compile")]
    [Authorize(Policy = PostingRuleAuthoringPermissions.Compile)]
    public async Task<IResult> CompileAsync([FromBody] PostingRulesAuthoringInput request, [FromServices] ICommandHandler<CompilePostingRules, PostingRulesAuthoringResult> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CompilePostingRules(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>fin.PostingRules.test: resolve sample events against the rules (side-effect free).</summary>
    [HttpPost("test")]
    [Authorize(Policy = PostingRuleAuthoringPermissions.Test)]
    public async Task<IResult> TestAsync([FromBody] PostingRulesAuthoringInput request, [FromServices] ICommandHandler<TestPostingRules, PostingRulesAuthoringResult> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new TestPostingRules(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>fin.PostingRules.previewImpact: replay recent postings through the proposed rules (side-effect free).</summary>
    [HttpPost("preview-impact")]
    [Authorize(Policy = PostingRuleAuthoringPermissions.PreviewImpact)]
    public async Task<IResult> PreviewImpactAsync([FromBody] PostingRulesAuthoringInput request, [FromServices] ICommandHandler<PreviewPostingRuleImpact, PostingRulesAuthoringResult> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new PreviewPostingRuleImpact(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>fin.PostingRules.submit: the maker sends a DRAFT for approval.</summary>
    [HttpPost("submit")]
    [Authorize(Policy = PostingRuleAuthoringPermissions.Submit)]
    public async Task<IResult> SubmitAsync([FromBody] PostingRulesSubmitInput request, [FromServices] ICommandHandler<SubmitPostingRules, PostingRulesAuthoringResult> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SubmitPostingRules(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>fin.PostingRules.decide: a checker approves (appending a rule-set version effective from its date) or rejects a SUBMITTED draft.</summary>
    [HttpPost("decide")]
    [Authorize(Policy = PostingRuleAuthoringPermissions.Decide)]
    public async Task<IResult> DecideAsync([FromBody] PostingRulesDecideInput request, [FromServices] ICommandHandler<DecidePostingRules, PostingRulesAuthoringResult> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new DecidePostingRules(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
