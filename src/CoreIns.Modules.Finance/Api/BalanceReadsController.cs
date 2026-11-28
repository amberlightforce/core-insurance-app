using CoreIns.SharedKernel.Identifiers;
using System.Text.Json;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Queries;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Finance.Api;

/// <summary><c>fin.Balance.get</c> (BO-FIN-02): <c>GET api/fin/v1/balances/{account}</c>, the balance of one account as the sum of its journal lines.</summary>
[ApiController]
[Route("api/fin/v1/balances")]
internal sealed class BalanceGetController : ControllerBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [HttpGet("{id}")]
    [Authorize(Policy = "fin.Balance.get")]
    public async Task<IResult> GetAsync(
        string id, [FromQuery] string? book, [FromQuery] string? view, [FromQuery] string? period, [FromQuery] string? dimensions, [FromQuery] string? knownAt,
        [FromServices] BalanceReader reader, [FromServices] IClock clock, CancellationToken cancellationToken)
    {
        var chosen = (view ?? BalanceReader.PeriodView).ToUpperInvariant();
        var parsed = BalanceReader.ParseDimensions(dimensions);
        var known = clock.Now;
        if (id.Length is 0 or > 32 || (book is not null && book.Length is 0 or > 32) || chosen is not (BalanceReader.PeriodView or BalanceReader.CumulativeView)
            || (period is not null && !PeriodClose.IsPeriodCode(period)) || (chosen == BalanceReader.PeriodView && period is null)
            || parsed is null || (knownAt is not null && !Instant.TryParse(knownAt, out known)))
        {
            return this.Problem(FinanceHttp.Invalid("account (max 32), book, view PERIOD (needs period) or CUMULATIVE, period yyyy-MM, dimensions key:value (productCode, coverageCode, chargeType, policyNumber) and knownAt must be valid."));
        }

        if (FinanceHttp.LegalEntity(HttpContext) is not (var legalEntity, _))
        {
            return this.Problem(DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The legal entity is not served by this stamp."));
        }

        var balance = await reader.GetAsync(legalEntity, id, book, chosen, period, parsed, known, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BalanceGetResponse { Balances = JsonSerializer.SerializeToElement(balance, Web) });
    }
}

/// <summary><c>fin.WrittenPremium.query</c> (BO-FIN-02): <c>GET api/fin/v1/written-premium/query</c>, written premium of record per month, book and currency.</summary>
[ApiController]
[Route("api/fin/v1/written-premium")]
internal sealed class WrittenPremiumController : ControllerBase
{
    [HttpGet("query")]
    [Authorize(Policy = "fin.WrittenPremium.query")]
    public async Task<IResult> QueryAsync([FromQuery] string? period, [FromQuery] string? dimensions, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] WrittenPremiumReader reader, CancellationToken cancellationToken)
    {
        // Only the product dimension narrows the written premium; any other dimension is refused rather than silently ignored.
        var parsed = BalanceReader.ParseDimensions(dimensions);
        string? product = null;
        var dimensionsValid = parsed is not null;
        if (parsed is { Count: > 0 })
        {
            dimensionsValid = parsed is [("l.product_code", _)];
            product = dimensionsValid ? parsed[0].Value : null;
        }

        if (limit is < 1 or > FinanceHttp.MaxLimit || (period is not null && !PeriodClose.IsPeriodCode(period)) || !dimensionsValid)
        {
            return this.Problem(FinanceHttp.Invalid($"limit must be within 1..{FinanceHttp.MaxLimit}, period yyyy-MM and dimensions productCode:value."));
        }

        var take = limit ?? FinanceHttp.DefaultLimit;
        if (FinanceHttp.LegalEntity(HttpContext) is not (var legalEntity, _))
        {
            return Results.Ok(new WrittenPremiumQueryPage { Items = [], NextCursor = null, Limit = take });
        }

        try
        {
            return Results.Ok(await reader.QueryAsync(legalEntity, period, product, cursor, take, cancellationToken).ConfigureAwait(false));
        }
        catch (FormatException)
        {
            return this.Problem(FinanceHttp.Invalid("cursor is malformed."));
        }
    }
}
