using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Billing.Services.Ai;

/// <summary>An unmatched payment in suspense.</summary>
internal sealed record SuspensePayment(decimal Amount, string? Reference, string? PayerName);

/// <summary>An open invoice that could settle it.</summary>
internal sealed record SuspenseCandidate(string InvoiceNo, decimal OpenAmount, string? Reference, string? PayerName);

/// <summary>Request of <c>bil.Suspense.suggestMatch</c>.</summary>
internal sealed record SuspenseMatchRequest(SuspensePayment? Payment, List<SuspenseCandidate>? Candidates);

/// <summary>Deterministic suspense-match scoring (AI-BIL-01 fallback template). A suggestion only: allocation stays the manual action.</summary>
internal static class SuspenseMatching
{
    public const string FeatureId = "AI-BIL-01";

    public static JsonObject Build(SuspensePayment payment, IReadOnlyList<SuspenseCandidate> candidates)
    {
        var ranked = candidates
            .Select(c => (Candidate: c, Score: Score(payment, c)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.InvoiceNo, StringComparer.Ordinal)
            .Take(3)
            .Select(x => (JsonNode?)new JsonObject { ["invoiceNo"] = x.Candidate.InvoiceNo, ["score"] = x.Score, ["exactAmount"] = x.Candidate.OpenAmount == payment.Amount })
            .ToArray();
        return new JsonObject { ["matches"] = new JsonArray(ranked), ["note"] = "Suggestion only; allocate manually." };
    }

    private static int Score(SuspensePayment p, SuspenseCandidate c)
    {
        var score = 0;
        if (c.OpenAmount == p.Amount)
        {
            score += 50;
        }

        if (!string.IsNullOrWhiteSpace(p.Reference)
            && (c.Reference?.Contains(p.Reference, StringComparison.OrdinalIgnoreCase) == true || p.Reference.Contains(c.InvoiceNo, StringComparison.OrdinalIgnoreCase)))
        {
            score += 40;
        }

        if (!string.IsNullOrWhiteSpace(p.PayerName) && string.Equals(p.PayerName.Trim(), c.PayerName?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }
}

/// <summary><c>bil.Suspense.suggestMatch</c> (AI-BIL-01) behind the AI gate (SL6-AI-FEATURES).</summary>
[ApiController]
[Route("api/bil/v1/suspense")]
internal sealed class SuspenseMatchController : ControllerBase
{
    /// <summary>Disabled: 503 PLT-ERR-AI-DISABLED and manual allocation is unchanged.</summary>
    [HttpPost("suggest-match")]
    [SkipIdempotency]
    [Authorize(Policy = "bil.Suspense.suggestMatch")]
    public async Task<IResult> SuggestAsync([FromBody] SuspenseMatchRequest request, CancellationToken cancellationToken)
    {
        if (request.Payment is null || request.Candidates is null || request.Candidates.Count > 200)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["payment"] = ["A payment and at most 200 candidate invoices are required."] });
        }

        // TODO(hardening): read the suspense item and open invoices from BIL instead of caller-supplied facts.
        var inputs = new JsonObject { ["payment"] = JsonSerializer.SerializeToNode(request.Payment), ["candidateCount"] = request.Candidates.Count };
        var result = await AiAssist.InvokeAsync(HttpContext, SuspenseMatching.FeatureId, inputs, cancellationToken).ConfigureAwait(false);
        return result.Refusal ?? Results.Ok(new { suggestion = SuspenseMatching.Build(request.Payment, request.Candidates), interactionId = result.Response!.InteractionId, featureId = SuspenseMatching.FeatureId, aiGenerated = true });
    }
}
