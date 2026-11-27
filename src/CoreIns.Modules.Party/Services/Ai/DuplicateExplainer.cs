using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Party.Api;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Context;
using CoreIns.Platform.Http;
using CoreIns.Platform.Time;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Party.Services.Ai;

/// <summary>One side of a suspected duplicate.</summary>
internal sealed record DuplicatePartySide(string? Name, string? Identifier, string? BirthDate, string? Address, string? Email);

/// <summary>Request of <c>pty.Party.explainDuplicate</c>: the two party ids only; attributes are read server-side with P2 values masked.</summary>
internal sealed record DuplicateExplainRequest(string? PartyIdA, string? PartyIdB);

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
    public async Task<IResult> ExplainAsync([FromBody] DuplicateExplainRequest request, [FromServices] PartyReader reader, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.PartyIdA, out var idA) || !Guid.TryParse(request.PartyIdB, out var idB) || idA == idB)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["partyIdA"] = ["Two different party ids (partyIdA and partyIdB) are required."] });
        }

        var result = await AiAssist.InvokeAsync(HttpContext, DuplicateExplanation.FeatureId, new JsonObject { ["partyIdA"] = request.PartyIdA, ["partyIdB"] = request.PartyIdB }, cancellationToken).ConfigureAwait(false);
        if (result.Refusal is { } refusal)
        {
            return refusal;
        }

        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        var protection = services.GetRequiredService<PartyProtection>();
        var now = services.GetRequiredService<IClock>().Now;
        var at = new TimePoint(new SharedKernel.BusinessDate(DateOnly.FromDateTime(now.ToUtcDateTime())), now);
        var a = await reader.GetAsync(protection.Current(context), context.LegalEntity!.Value, idA, null, at, revealP2: false, cancellationToken).ConfigureAwait(false);
        var b = await reader.GetAsync(protection.Current(context), context.LegalEntity!.Value, idB, null, at, revealP2: false, cancellationToken).ConfigureAwait(false);
        if (a is null || b is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new { suggestion = DuplicateExplanation.Build(Side(a), Side(b)), interactionId = result.Response!.InteractionId, featureId = DuplicateExplanation.FeatureId, aiGenerated = true });
    }

    /// <summary>Comparable attributes of a stored party. P2 values stay masked, so identifiers compare by their display suffix and the birth date is not compared.</summary>
    private static DuplicatePartySide Side(PartyView party)
    {
        var name = party.Names.FirstOrDefault(n => n.Form == PartyNameView.FormValue.Native) ?? (party.Names.Count > 0 ? party.Names[0] : null);
        var display = name is null ? null : name.OrganisationName ?? string.Join(' ', new[] { name.GivenNames, name.FamilyName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var identifier = (party.Identifiers.Count > 0 ? party.Identifiers[0] : null);
        var address = (party.Addresses.FirstOrDefault(x => x.Primary) ?? (party.Addresses.Count > 0 ? party.Addresses[0] : null))?.FormattedLines;
        var email = party.ContactPoints.FirstOrDefault(c => c.Type == ContactPointType.Email);
        return new DuplicatePartySide(
            display,
            identifier is null ? null : $"{identifier.Scheme}:{identifier.Value}",
            null,
            address is null ? null : string.Join(", ", address),
            email?.Value);
    }
}
