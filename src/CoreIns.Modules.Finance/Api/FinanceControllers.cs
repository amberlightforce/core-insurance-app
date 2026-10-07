using System.Globalization;
using CoreIns.Modules.Finance.Contracts.Api;
using CoreIns.Modules.Finance.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Finance.Api;

/// <summary>Permissions of the FIN operations (the operations' <c>x-permission</c>), mapped to roles in <c>Platform:Permissions:Grants</c>.</summary>
internal static class FinancePermissions
{
    public const string JournalGet = "fin.Journal.get";
    public const string JournalQuery = "fin.Journal.query";
    public const string PostingRulesList = "fin.PostingRules.list";
}

/// <summary>Shared request helpers of the FIN controllers.</summary>
internal static class FinanceHttp
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static IResult Problem(this ControllerBase controller, DomainError error) =>
        controller.HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, controller.HttpContext);

    public static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.FIN, "VALIDATION", detail);

    public static bool TryDate(string? text, out BusinessDate? date)
    {
        date = null;
        if (text is null)
        {
            return true;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            date = new BusinessDate(value);
            return true;
        }

        return false;
    }

    /// <summary>The caller's legal entity (stamp default when the request names none); null when this stamp does not serve it.</summary>
    public static (LegalEntityId Id, LegalEntityCode Code)? LegalEntity(HttpContext http)
    {
        var context = http.RequestServices.GetRequiredService<RequestContext>();
        if (context.LegalEntity is not { } code)
        {
            return null;
        }

        try
        {
            return (http.RequestServices.GetRequiredService<ILegalEntityDirectory>().Resolve(code), code);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>REST facade of <c>fin.Journal.get</c> and <c>fin.Journal.query</c> (REQ-FIN-078, -079).</summary>
[ApiController]
[Route("api/fin/v1/journals")]
internal sealed class JournalsController : ControllerBase
{
    /// <summary>fin.Journal.query: journals with lines by policy business key, source event type, book and accounting-date range.</summary>
    [HttpGet("query")]
    [Authorize(Policy = FinancePermissions.JournalQuery)]
    public async Task<IResult> QueryAsync(
        [FromQuery] string? policyNumber,
        [FromQuery] string? sourceEventType,
        [FromQuery] string? book,
        [FromQuery] string? accountingDateFrom,
        [FromQuery] string? accountingDateTo,
        [FromQuery] string? knownAt,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        [FromServices] JournalReader reader,
        [FromServices] IClock clock,
        CancellationToken cancellationToken)
    {
        if (!FinanceHttp.TryDate(accountingDateFrom, out var from) || !FinanceHttp.TryDate(accountingDateTo, out var to))
        {
            return this.Problem(FinanceHttp.Invalid("accountingDateFrom and accountingDateTo must be dates (yyyy-MM-dd)."));
        }

        var known = clock.Now;
        if (knownAt is not null && !Instant.TryParse(knownAt, out known))
        {
            return this.Problem(FinanceHttp.Invalid("knownAt must be an instant."));
        }

        if (limit is < 1 or > FinanceHttp.MaxLimit || (policyNumber is not null && !PolicyNumber.TryParse(policyNumber, out _)))
        {
            return this.Problem(FinanceHttp.Invalid($"limit must be within 1..{FinanceHttp.MaxLimit} and policyNumber a business number."));
        }

        if (FinanceHttp.LegalEntity(HttpContext) is not (var legalEntity, _))
        {
            return Results.Ok(new JournalQueryPage { Items = [], NextCursor = null, Limit = limit ?? FinanceHttp.DefaultLimit });
        }

        try
        {
            var page = await reader.QueryAsync(legalEntity, new JournalFilter(policyNumber, sourceEventType, book, from, to, known), cursor, limit ?? FinanceHttp.DefaultLimit, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(page);
        }
        catch (FormatException)
        {
            return this.Problem(FinanceHttp.Invalid("cursor is malformed."));
        }
    }

    /// <summary>fin.Journal.get by id or journal number.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = FinancePermissions.JournalGet)]
    public async Task<IResult> GetAsync(string id, [FromQuery] string? knownAt, [FromServices] JournalReader reader, [FromServices] IClock clock, CancellationToken cancellationToken)
    {
        var known = clock.Now;
        if (knownAt is not null && !Instant.TryParse(knownAt, out known))
        {
            return this.Problem(FinanceHttp.Invalid("knownAt must be an instant."));
        }

        var view = FinanceHttp.LegalEntity(HttpContext) is (var legalEntity, _)
            ? await reader.GetAsync(legalEntity, id, known, cancellationToken).ConfigureAwait(false)
            : null;
        return view is null
            ? this.Problem(DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The journal does not exist."))
            : Results.Ok(new JournalGetResponse { Journal = view });
    }
}

/// <summary>REST facade of <c>fin.PostingRules.list</c> (REQ-FIN-048, -049, -052).</summary>
[ApiController]
[Route("api/fin/v1/posting-rules")]
internal sealed class PostingRulesController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = FinancePermissions.PostingRulesList)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? book,
        [FromQuery] string? validAt,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        [FromServices] PostingRuleReader reader,
        [FromServices] IClock clock,
        CancellationToken cancellationToken)
    {
        if (!FinanceHttp.TryDate(validAt, out var at) || limit is < 1 or > FinanceHttp.MaxLimit)
        {
            return this.Problem(FinanceHttp.Invalid($"validAt must be a date (yyyy-MM-dd) and limit within 1..{FinanceHttp.MaxLimit}."));
        }

        var date = at ?? new BusinessDate(DateOnly.FromDateTime(clock.Now.ToUtcDateTime()));
        if (FinanceHttp.LegalEntity(HttpContext) is not (_, var code))
        {
            return Results.Ok(new PostingRulesListPage { Items = [], NextCursor = null, Limit = limit ?? FinanceHttp.DefaultLimit });
        }

        try
        {
            return Results.Ok(await reader.ListAsync(code, book, date, cursor, limit ?? FinanceHttp.DefaultLimit, cancellationToken).ConfigureAwait(false));
        }
        catch (FormatException)
        {
            return this.Problem(FinanceHttp.Invalid("cursor is malformed."));
        }
    }
}
