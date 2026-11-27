using CoreIns.Modules.Channels.Queries;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Api;

/// <summary>Problem responses shared by the portal controllers: not found never reveals whether the record exists.</summary>
internal static class PortalProblems
{
    public static IResult NotFound(HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "Not found."), http);

    public static IResult NotPermitted(HttpContext http) =>
        http.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(DomainError.Of(ModuleCode.CHN, "NOT-PERMITTED", "You are not permitted to do this."), http);
}

/// <summary>REST facade of <c>chn.PortalBilling.*</c> (BO-CHN-04): the customer statement, open invoices, refund status and pay-now.</summary>
[ApiController]
[Route("api/chn/v1/portal/billing")]
internal sealed class PortalBillingController(PortalBillingReader reader) : ControllerBase
{
    /// <summary>chn.PortalBilling.summary.</summary>
    [HttpGet("summary")]
    [Authorize(Policy = "chn.PortalBilling.summary")]
    public async Task<IResult> SummaryAsync(CancellationToken cancellationToken) =>
        await reader.SummaryAsync(cancellationToken).ConfigureAwait(false) is { } body ? Results.Ok(body) : PortalProblems.NotFound(HttpContext);

    /// <summary>chn.PortalBilling.invoices.</summary>
    [HttpGet("invoices")]
    [Authorize(Policy = "chn.PortalBilling.invoices")]
    public async Task<IResult> InvoicesAsync([FromQuery] string? state, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        await reader.InvoicesAsync(state, cursor, limit, cancellationToken).ConfigureAwait(false) is { } body ? Results.Ok(body) : PortalProblems.NotFound(HttpContext);

    /// <summary>chn.PortalBilling.refunds.</summary>
    [HttpGet("refunds")]
    [Authorize(Policy = "chn.PortalBilling.refunds")]
    public async Task<IResult> RefundsAsync(CancellationToken cancellationToken) =>
        await reader.RefundsAsync(cancellationToken).ConfigureAwait(false) is { } body ? Results.Ok(body) : PortalProblems.NotFound(HttpContext);

    /// <summary>chn.PortalBilling.payNow.</summary>
    [HttpPost("invoices/{id}/pay-now")]
    [Authorize(Policy = "chn.PortalBilling.payNow")]
    public async Task<IResult> PayNowAsync(string id, CancellationToken cancellationToken) =>
        await reader.PayNowAsync(id, cancellationToken).ConfigureAwait(false) is { } body ? Results.Ok(body) : PortalProblems.NotFound(HttpContext);
}
