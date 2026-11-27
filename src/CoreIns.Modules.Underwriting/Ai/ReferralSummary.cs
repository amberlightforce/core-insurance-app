using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Underwriting.Services;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using CoreIns.Modules.Underwriting.Contracts.Api;

namespace CoreIns.Modules.Underwriting.Ai;

/// <summary>Request of <c>uw.Referral.summarize</c>: the referral (job) id only; the reasons and facts are loaded server-side.</summary>
internal sealed record ReferralSummaryRequest(string? ReferralId);

/// <summary>The server-loaded facts a summary is built from.</summary>
internal sealed record ReferralSummaryFacts(string ReferralId, List<string> Reasons, decimal? SumInsured, string? Product);

/// <summary>Deterministic decision-support summary (AI-UW-01 fallback template). The decision stays the underwriter's.</summary>
internal static class ReferralSummaryTemplate
{
    public const string FeatureId = "AI-UW-01";

    public static JsonObject Build(ReferralSummaryFacts request)
    {
        var reasons = request.Reasons;
        var text = $"Referral {request.ReferralId}{(request.Product is null ? string.Empty : $" for {request.Product}")}. "
            + (reasons.Count == 0 ? "No referral reasons were supplied." : $"Referred because: {string.Join("; ", reasons)}.")
            + (request.SumInsured is { } sum ? $" Sum insured {sum:0.00}." : string.Empty);
        return new JsonObject
        {
            ["summary"] = text,
            ["reasonCount"] = reasons.Count,
            ["considerations"] = new JsonArray("Verify the referral reasons against the submission.", "Apply the authority limit before deciding."),
        };
    }
}

/// <summary><c>uw.Referral.summarize</c> (AI-UW-01) behind the AI gate (SL6-AI-FEATURES).</summary>
[ApiController]
[Route("api/uw/v1/referrals")]
internal sealed class ReferralSummaryController : ControllerBase
{
    /// <summary>Disabled: 503 PLT-ERR-AI-DISABLED and the referral list/decide flow is unchanged.</summary>
    [HttpPost("summarize")]
    [SkipIdempotency]
    [Authorize(Policy = "uw.Referral.summarize")]
    public async Task<IResult> SummarizeAsync([FromBody] ReferralSummaryRequest request, [FromServices] ReferralQueries queries, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ReferralId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["referralId"] = ["A referral id is required."] });
        }

        var result = await AiAssist.InvokeAsync(HttpContext, ReferralSummaryTemplate.FeatureId, new JsonObject { ["referralId"] = request.ReferralId }, cancellationToken).ConfigureAwait(false);
        if (result.Refusal is { } refusal)
        {
            return refusal;
        }

        // Same legal-entity-scoped read as uw.Referral.get; another entity's referral is "not found".
        var loaded = await queries.GetAsync(request.ReferralId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(loaded.Error, HttpContext);
        }

        var referral = loaded.Value.Referral;
        var reasons = referral.Summary.Reasons.Where(r => r.Status == IssueStatusCode.Open).Select(r => r.Limit is null ? $"{r.IssueType} ({r.RuleId})" : $"{r.IssueType} ({r.RuleId}): {r.Observed} vs limit {r.Limit}").ToList();
        var facts = new ReferralSummaryFacts(request.ReferralId, reasons, referral.Facts?.Vehicle.Value?.Amount, referral.Summary.ProductCode);
        return Results.Ok(new { suggestion = ReferralSummaryTemplate.Build(facts), interactionId = result.Response!.InteractionId, featureId = ReferralSummaryTemplate.FeatureId, aiGenerated = true });
    }
}
