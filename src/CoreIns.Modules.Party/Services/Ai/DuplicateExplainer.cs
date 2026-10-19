using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Party.Services.Ai;

/// <summary>One side of a suspected duplicate.</summary>
internal sealed record DuplicatePartySide(string? Name, string? Identifier, string? BirthDate, string? Address, string? Email);

/// <summary>Request of <c>pty.Party.explainDuplicate</c>.</summary>
internal sealed record DuplicateExplainRequest(DuplicatePartySide? A, DuplicatePartySide? B);

/// <summary>Deterministic duplicate explainer (AI-PTY-01 fallback template): which attributes agree and which differ. A suggestion only; merging stays a steward action.</summary>
internal static class DuplicateExplanation
{
    public const string FeatureId = "AI-PTY-01";

    public static JsonObject Build(DuplicatePartySide a, DuplicatePartySide b)
    {
        var agree = new JsonArray();
        var differ = new JsonArray();
        Compare("name", a.Name, b.Name, agree, differ);
        Compare("identifier", a.Identifier, b.Identifier, agree, differ);
        Compare("birthDate", a.BirthDate, b.BirthDate, agree, differ);
        Compare("address", a.Address, b.Address, agree, differ);
        Compare("email", a.Email, b.Email, agree, differ);
        var strong = agree.Any(n => n!.GetValue<string>() is "identifier" or "birthDate") && agree.Count >= 2;
        return new JsonObject
        {
            ["agreeingFields"] = agree,
            ["differingFields"] = differ,
            ["likelihood"] = strong ? "LIKELY_DUPLICATE" : agree.Count >= 1 ? "POSSIBLE_DUPLICATE" : "UNLIKELY",
            ["explanation"] = $"{agree.Count} attribute(s) agree and {differ.Count} differ. A steward decides whether to merge.",
        };
    }

    private static void Compare(string field, string? a, string? b, JsonArray agree, JsonArray differ)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return;
        }

        (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase) ? agree : differ).Add(field);
    }
}

/// <summary><c>pty.Party.explainDuplicate</c> (AI-PTY-01) behind the AI gate (SL6-AI-FEATURES).</summary>
[ApiController]
[Route("api/pty/v1/parties")]
internal sealed class DuplicateExplainController : ControllerBase
{
    /// <summary>Disabled: 503 PLT-ERR-AI-DISABLED and party search/merge are unchanged.</summary>
    [HttpPost("explain-duplicate")]
    [SkipIdempotency]
    [Authorize(Policy = "pty.Party.explainDuplicate")]
    public async Task<IResult> ExplainAsync([FromBody] DuplicateExplainRequest request, CancellationToken cancellationToken)
    {
        if (request.A is null || request.B is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["a"] = ["Both parties (a and b) are required."] });
        }

        // TODO(hardening): accept two party ids and read them through the party reader instead of caller-supplied attributes.
        var inputs = new JsonObject { ["a"] = JsonSerializer.SerializeToNode(request.A), ["b"] = JsonSerializer.SerializeToNode(request.B) };
        var result = await AiAssist.InvokeAsync(HttpContext, DuplicateExplanation.FeatureId, inputs, cancellationToken).ConfigureAwait(false);
        return result.Refusal ?? Results.Ok(new { suggestion = DuplicateExplanation.Build(request.A, request.B), interactionId = result.Response!.InteractionId, featureId = DuplicateExplanation.FeatureId, aiGenerated = true });
    }
}
