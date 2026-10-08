using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Servicing;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Rating.Api;

/// <summary>Permission of the proration operation (the contract's <c>x-permission</c>).</summary>
internal static class ProrationPermissions
{
    public const string Prorate = "rat.Proration.prorate";
}

/// <summary>
/// REST facade of <c>rat.Proration.prorate</c> (a pure query, POST because of the body). The contract's request and response members are
/// still untyped JSON (SL3-CONTRACTS types them), so this controller documents the shape it reads and writes:
/// <code>
/// term:       { "from": "2026-03-01", "to": "2027-03-01", "currency": "EUR" }
/// convention: { "name": "TERM_RATIO", "declared": ["TERM_RATIO"] }
/// periods:    [ { "segmentId": "S1", "from": "2026-03-01", "to": "2026-06-29" } ]
/// annualRates:[ { "segmentId": "S1", "lineId": "L1", "elementId": "V1", "chargeType": "PREM-MTPL", "annualRate": "365.0000", "handling": "PRORATABLE" } ]
/// </code>
/// Dates are whole Europe/Athens calendar dates, half-open; amounts and rates travel as decimal strings. The response fills
/// <c>amounts</c>, <c>fractions</c>, <c>residuals</c> and <c>explanation</c> in request-line order.
/// </summary>
[ApiController]
[Route("api/rat/v1")]
internal sealed class ProrationController : ControllerBase
{
    /// <summary>rat.Proration.prorate.</summary>
    [HttpPost("proration/prorate")]
    [SkipIdempotency]
    [Authorize(Policy = ProrationPermissions.Prorate)]
    public async Task<IResult> ProrateAsync(
        [FromBody] ProrationProrateRequest request, [FromServices] IRatingProrationEngine engine, CancellationToken cancellationToken)
    {
        try
        {
            var mapped = ProrationHttp.ToRequest(request);
            var result = await engine.ProrateAsync(mapped, cancellationToken).ConfigureAwait(false);
            return Results.Ok(ProrationHttp.ToResponse(result, request.Explain == true));
        }
        catch (DomainException ex)
        {
            return HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(ex.Error, HttpContext);
        }
    }
}

/// <summary>Maps the untyped JSON of <c>rat.Proration.prorate</c> to and from the in-process contract.</summary>
internal static class ProrationHttp
{
    public static ProrationRequest ToRequest(ProrationProrateRequest body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var term = Object(body.Term, "term");
        var convention = Object(body.Convention, "convention");
        var periods = Array(body.Periods, "periods");
        var rates = Array(body.AnnualRates, "annualRates");
        if (!Currency.TryFromCode(Text(term, "currency"), out var currency))
        {
            throw Error("INPUT", "term.currency is not a currency code.");
        }

        var declared = convention.TryGetProperty("declared", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty).ToList()
            : [];
        var bySegment = rates.GroupBy(r => Text(r, "segmentId"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var segments = new List<ProrationSegmentInput>();
        foreach (var period in periods)
        {
            var id = Text(period, "segmentId");
            var lines = (bySegment.GetValueOrDefault(id) ?? []).Select(r => new ProrationLineInput(
                Text(r, "lineId"), Text(r, "elementId"), Text(r, "chargeType"), Decimal(r, "annualRate"), Handling(r))).ToList();
            segments.Add(new ProrationSegmentInput(id, Date(period, "from"), Date(period, "to"), lines));
        }

        if (bySegment.Keys.Any(k => segments.All(s => s.SegmentId != k)))
        {
            throw Error("INPUT", "An annual rate names a segment that is not in periods.");
        }

        return new ProrationRequest(
            currency, Date(term, "from"), Date(term, "to"), Text(convention, "name"), declared, segments, body.ConfigurationHash);
    }

    public static ProrationProrateResponse ToResponse(ProrationResult result, bool explain)
    {
        var fractions = result.Lines.Select(l => new { l.SegmentId, l.LineId, l.Days, l.DaysDenominator, fraction = Format(l.Fraction) });
        var residuals = result.Lines.Select(l => new { l.SegmentId, l.LineId, residual = Format(l.Residual.Amount), l.Residual.Currency.Code });
        return new ProrationProrateResponse
        {
            Amounts = [.. result.Lines.Select(l => l.Amount)],
            Fractions = JsonSerializer.SerializeToElement(fractions),
            Residuals = JsonSerializer.SerializeToElement(residuals),
            Explanation = explain
                ? JsonSerializer.SerializeToElement(new
                {
                    result.Convention,
                    result.TermDays,
                    result.RoundingLegalStatus,
                    result.Provisional,
                    lines = result.Lines.Select(l => new { l.SegmentId, l.LineId, l.Days, l.DaysDenominator, unrounded = Format(l.Unrounded), roundingRule = l.RoundingRuleKey }),
                })
                : null,
        };
    }

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static JsonElement Object(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e ? e : throw Error("INPUT", $"{name} must be an object.");

    private static IReadOnlyList<JsonElement> Array(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Array } e ? [.. e.EnumerateArray()] : throw Error("INPUT", $"{name} must be an array.");

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw Error("INPUT", $"{name} is required.");

    private static BusinessDate Date(JsonElement element, string name) =>
        BusinessDate.TryParse(Text(element, name), out var date) ? date : throw Error("INPUT", $"{name} must be a date (yyyy-MM-dd).");

    private static decimal Decimal(JsonElement element, string name) =>
        decimal.TryParse(Text(element, name), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Error("INPUT", $"{name} must be a decimal string.");

    private static ProrationHandling Handling(JsonElement element) =>
        element.TryGetProperty("handling", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.ToUpperInvariant() switch
            {
                "PRORATABLE" => ProrationHandling.Proratable,
                "FLAT" => ProrationHandling.Flat,
                "FULLY_EARNED" => ProrationHandling.FullyEarned,
                _ => throw Error("INPUT", "handling must be PRORATABLE, FLAT or FULLY_EARNED."),
            }
            : ProrationHandling.Proratable;

    private static DomainException Error(string code, string detail) => new(DomainError.Of(ModuleCode.RAT, code, detail));
}
