using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Claims.Services.Ai;

/// <summary>Request of <c>clm.Fnol.assist</c>: the caller's free text.</summary>
internal sealed record FnolAssistRequest(string? Text);

/// <summary>Deterministic fallback template of the FNOL assistant: pulls the obvious facts out of free text. A suggestion only.</summary>
internal static partial class FnolDraft
{
    public const string FeatureId = "AI-CLM-01";
    public const int MaxLength = 4000;

    private static readonly (string Cause, string[] Words)[] Causes =
    [
        ("COLLISION", ["collision", "crash", "accident", "σύγκρουση", "τράκαρ", "ατύχημα"]),
        ("THEFT", ["theft", "stolen", "burglary", "κλοπή", "κλέψ"]),
        ("FIRE", ["fire", "πυρκαγιά", "φωτιά"]),
        ("GLASS", ["windscreen", "windshield", "glass", "παρμπρίζ", "τζάμι"]),
        ("WATER", ["flood", "water", "leak", "πλημμύρα", "νερό", "διαρροή"]),
    ];

    public static JsonObject Build(string text)
    {
        var lower = text.ToLowerInvariant();
        var cause = Causes.FirstOrDefault(c => c.Words.Any(w => lower.Contains(w, StringComparison.Ordinal))).Cause;
        var iso = IsoDate().Match(text);
        var local = LocalDate().Match(text);
        var date = iso.Success ? iso.Value
            : local.Success ? $"{local.Groups[3].Value}-{local.Groups[2].Value.PadLeft(2, '0')}-{local.Groups[1].Value.PadLeft(2, '0')}"
            : null;
        var plateMatch = Plate().Match(text);
        var plate = plateMatch.Success ? plateMatch.Value.Replace(" ", "-", StringComparison.Ordinal) : null;
        var missing = new JsonArray();
        if (date is null)
        {
            missing.Add("lossDate");
        }

        if (cause is null)
        {
            missing.Add("cause");
        }

        var summary = text.Trim();
        return new JsonObject
        {
            ["description"] = summary.Length > 500 ? summary[..500] : summary,
            ["lossDate"] = date,
            ["cause"] = cause,
            ["vehiclePlate"] = plate,
            ["missingFields"] = missing,
        };
    }

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(\d{1,2})[/.](\d{1,2})[/.](\d{4})\b")]
    private static partial Regex LocalDate();

    [GeneratedRegex(@"\b[A-ZΑ-Ω]{3}[ -]?\d{4}\b")]
    private static partial Regex Plate();
}

/// <summary><c>clm.Fnol.assist</c> (AI-CLM-01): drafts an FNOL description from free text behind the AI gate (SL6-AI-FEATURES).</summary>
[ApiController]
[Route("api/clm/v1/fnol")]
internal sealed class FnolAssistController : ControllerBase
{
    /// <summary>Disabled: 503 PLT-ERR-AI-DISABLED and the manual FNOL submit is unchanged. Allowed: draft + interaction id; nothing is stored in CLM.</summary>
    [HttpPost("assist")]
    [SkipIdempotency]
    [Authorize(Policy = "clm.Fnol.assist")]
    public async Task<IResult> AssistAsync([FromBody] FnolAssistRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > FnolDraft.MaxLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"Required, at most {FnolDraft.MaxLength} characters."] });
        }

        // TODO(hardening): the free text may carry personal data; minimise/redact before it reaches a real provider.
        var result = await AiAssist.InvokeAsync(HttpContext, FnolDraft.FeatureId, new JsonObject { ["text"] = request.Text }, cancellationToken).ConfigureAwait(false);
        if (result.Refusal is { } refusal)
        {
            return refusal;
        }

        return Results.Ok(new { suggestion = FnolDraft.Build(request.Text), interactionId = result.Response!.InteractionId, featureId = FnolDraft.FeatureId, aiGenerated = true });
    }
}
