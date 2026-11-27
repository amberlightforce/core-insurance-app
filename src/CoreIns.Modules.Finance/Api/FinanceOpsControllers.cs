using System.Text.RegularExpressions;
using CoreIns.Modules.Finance.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Finance.Api;

/// <summary>Permissions of the finance operations reads (the operations' <c>x-permission</c>).</summary>
internal static class FinanceOpsPermissions
{
    public const string TrialBalance = "fin.Balance.trialBalance";
    public const string IntakeExceptionList = "fin.IntakeException.list";
    public const string PeriodList = "fin.Period.list";
}

/// <summary>REST facade of <c>fin.Balance.trialBalance</c> (REQ-FIN-078, -100): per-account debit/credit of one calendar month.</summary>
[ApiController]
[Route("api/fin/v1/balances")]
internal sealed class BalancesController : ControllerBase
{
    [HttpGet("trial-balance")]
    [Authorize(Policy = FinanceOpsPermissions.TrialBalance)]
    public async Task<IResult> TrialBalanceAsync([FromQuery] string? period, [FromQuery] string? book, [FromServices] FinanceOpsReader reader, CancellationToken cancellationToken)
    {
        if (period is null || !Regex.IsMatch(period, "^[0-9]{4}-(0[1-9]|1[0-2])$", RegexOptions.None, TimeSpan.FromSeconds(1)) || (book is not null && book.Length is 0 or > 32))
        {
            return this.Problem(FinanceHttp.Invalid("period must be yyyy-MM and book at most 32 characters."));
        }

        if (FinanceHttp.LegalEntity(HttpContext) is not (var legalEntity, _))
        {
            return Results.Ok(new TrialBalanceResult(period, book, "EUR", [], "0.00", "0.00"));
        }

        return Results.Ok(await reader.TrialBalanceAsync(legalEntity, period, book, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>REST facade of <c>fin.IntakeException.list</c> (REQ-FIN-080): the suspended business events.</summary>
[ApiController]
[Route("api/fin/v1/intake-exceptions")]
internal sealed class IntakeExceptionsController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = FinanceOpsPermissions.IntakeExceptionList)]
    public async Task<IResult> ListAsync([FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] FinanceOpsReader reader, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > FinanceHttp.MaxLimit)
        {
            return this.Problem(FinanceHttp.Invalid($"limit must be within 1..{FinanceHttp.MaxLimit}."));
        }

        var take = limit ?? FinanceHttp.DefaultLimit;
        if (FinanceHttp.LegalEntity(HttpContext) is not (var legalEntity, _))
        {
            return Results.Ok(new OpsPage<IntakeExceptionRow>([], null, take));
        }

        try
        {
            return Results.Ok(await reader.IntakeExceptionsAsync(legalEntity, cursor, take, cancellationToken).ConfigureAwait(false));
        }
        catch (FormatException)
        {
            return this.Problem(FinanceHttp.Invalid("cursor is malformed."));
        }
    }
}

/// <summary>REST facade of <c>fin.Period.list</c> (REQ-FIN-007, -232): accounting periods and their state.</summary>
[ApiController]
[Route("api/fin/v1/periods")]
internal sealed class PeriodsController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = FinanceOpsPermissions.PeriodList)]
    public async Task<IResult> ListAsync([FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] FinanceOpsReader reader, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > FinanceHttp.MaxLimit)
        {
            return this.Problem(FinanceHttp.Invalid($"limit must be within 1..{FinanceHttp.MaxLimit}."));
        }

        var take = limit ?? FinanceHttp.DefaultLimit;
        if (FinanceHttp.LegalEntity(HttpContext) is not (var legalEntity, _))
        {
            return Results.Ok(new OpsPage<PeriodRow>([], null, take));
        }

        try
        {
            return Results.Ok(await reader.PeriodsAsync(legalEntity, cursor, take, cancellationToken).ConfigureAwait(false));
        }
        catch (FormatException)
        {
            return this.Problem(FinanceHttp.Invalid("cursor is malformed."));
        }
    }
}
