using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Underwriting.Ai;

/// <summary>Request of <c>uw.Referral.summarize</c>: the referral facts the underwriter sees.</summary>
internal sealed record ReferralSummaryRequest(string? ReferralId, List<string>? Reasons, decimal? SumInsured, string? Product);

/// <summary>Deterministic decision-support summary (AI-UW-01 fallback template). The decision stays the underwriter's.</summary>
internal static class ReferralSummaryTemplate
{
    public const string FeatureId = "AI-UW-01";

    public static JsonObject Build(ReferralSummaryRequest request)
    {
        var reasons = request.Reasons ?? [];
        var text = $"Referral {request.ReferralId ?? "(unspecified)"}{(request.Product is null ? string.Empty : $" for {request.Product}")}. "
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
    public async Task<IResult> SummarizeAsync([FromBody] ReferralSummaryRequest request, CancellationToken cancellationToken)
    {
        // TODO(hardening): load the referral by id through the referral reader instead of caller-supplied facts.
        var result = await AiAssist.InvokeAsync(HttpContext, ReferralSummaryTemplate.FeatureId, JsonSerializer.SerializeToNode(request)!.AsObject(), cancellationToken).ConfigureAwait(false);
        return result.Refusal ?? Results.Ok(new { suggestion = ReferralSummaryTemplate.Build(request), interactionId = result.Response!.InteractionId, featureId = ReferralSummaryTemplate.FeatureId, aiGenerated = true });
    }
}
