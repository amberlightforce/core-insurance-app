using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the ledger reads (<c>x-permission</c>).</summary>
internal static class LedgerPermissions
{
    public const string Query = "bil.Ledger.query";
    public const string Balance = "bil.Ledger.balance";
    public const string Fin = "bil.Reconciliation.fin";
}

/// <summary>REST facade of the sub-ledger reads (BO-BIL-12): entries, balances and the day control totals FIN reconciles. Read-only.</summary>
[ApiController]
[Route("api/bil/v1")]
internal sealed class LedgerController : ControllerBase
{
    /// <summary>bil.Ledger.query: entries with lines, oldest first.</summary>
    [HttpGet("ledger/query")]
    [Authorize(Policy = LedgerPermissions.Query)]
    public async Task<IResult> QueryAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] Guid? billingAccountId, [FromQuery] string? account, [FromQuery] string? entryType,
        [FromQuery] string? currency, [FromQuery] string? from, [FromQuery] string? to, [FromServices] LedgerReader reader, CancellationToken cancellationToken)
    {
        var (filter, error) = Filter(billingAccountId, account, entryType, currency, from, to);
        return error is not null
            ? HttpResults.Problem(error, HttpContext)
            : Results.Ok(await reader.QueryAsync(filter!, cursor, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>bil.Ledger.balance: debit, credit and net per account and currency.</summary>
    [HttpGet("ledger/balance")]
    [Authorize(Policy = LedgerPermissions.Balance)]
    public async Task<IResult> BalanceAsync(
        [FromQuery] Guid? billingAccountId, [FromQuery] string? account, [FromQuery] string? entryType, [FromQuery] string? currency,
        [FromQuery] string? from, [FromQuery] string? to, [FromServices] LedgerReader reader, CancellationToken cancellationToken)
    {
        var (filter, error) = Filter(billingAccountId, account, entryType, currency, from, to);
        return error is not null
            ? HttpResults.Problem(error, HttpContext)
            : Results.Ok(await reader.BalanceAsync(filter!, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>bil.Reconciliation.fin: the control totals of one accounting day; read-only, never triggers the push to FIN.</summary>
    [HttpPost("reconciliation/fin")]
    [SkipIdempotency]
    [Authorize(Policy = LedgerPermissions.Fin)]
    public async Task<IResult> FinAsync([FromBody] ReconciliationFinRequest request, [FromServices] FinControlTotals totals, CancellationToken cancellationToken)
    {
        var date = request.AccountingDate;
        return (await totals.ComputeAsync(LegalEntity(), date, request.SubLedgerAccount, request.EventType, request.IncludeEntryIds == true, cancellationToken).ConfigureAwait(false))
            .ToHttpResult(HttpContext);
    }

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }

    private (LedgerFilter? Filter, DomainError? Error) Filter(Guid? billingAccountId, string? account, string? entryType, string? currency, string? from, string? to)
    {
        BusinessDate? fromDate = null;
        BusinessDate? toDate = null;
        if (from is not null)
        {
            if (!BusinessDate.TryParse(from, out var f))
            {
                return (null, DomainError.Of(ModuleCode.BIL, "VALIDATION", "from must be a date, yyyy-MM-dd."));
            }

            fromDate = f;
        }

        if (to is not null)
        {
            if (!BusinessDate.TryParse(to, out var t))
            {
                return (null, DomainError.Of(ModuleCode.BIL, "VALIDATION", "to must be a date, yyyy-MM-dd."));
            }

            toDate = t;
        }

        if (fromDate is { } a && toDate is { } b && a > b)
        {
            return (null, DomainError.Of(ModuleCode.BIL, "VALIDATION", "from must not be after to."));
        }

        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)))
        {
            return (null, DomainError.Of(ModuleCode.BIL, "VALIDATION", "currency must be an ISO 4217 code in capitals."));
        }

        return (new LedgerFilter(LegalEntity(), billingAccountId is { } id ? new BillingAccountId(id) : null, account, entryType, currency, fromDate, toDate), null);
    }
}
