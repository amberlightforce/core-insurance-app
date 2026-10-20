using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the NX-HD-08c read APIs (<c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
internal static class BillingReadPermissions
{
    public const string TaxLevyPeriods = "bil.TaxLevy.periods";
    public const string TaxLevyDetail = "bil.TaxLevy.detail";
    public const string MigratedRecordList = "bil.MigratedRecord.list";
}

/// <summary>REST facade of bil.TaxLevy reads (contracts/openapi/bil.yaml): IPT and levy payable accruals per month. <c>requestLevyPayment</c> is not served yet.</summary>
[ApiController]
[Route("api/bil/v1/tax-levy")]
internal sealed class TaxLevyController : ControllerBase
{
    /// <summary>bil.TaxLevy.periods: accrual totals per month, optionally one <c>period</c> (yyyy-MM).</summary>
    [HttpGet("periods")]
    [Authorize(Policy = BillingReadPermissions.TaxLevyPeriods)]
    public async Task<IResult> PeriodsAsync([FromQuery] string? period, [FromServices] TaxLevyReader reader, CancellationToken cancellationToken) =>
        period is not null && !TaxLevyReader.IsPeriod(period)
            ? BadPeriod()
            : Results.Ok(await reader.PeriodsAsync(LegalEntity(), period, cancellationToken).ConfigureAwait(false));

    /// <summary>bil.TaxLevy.detail: accruals per month and ledger rule, optionally one <c>period</c> (yyyy-MM).</summary>
    [HttpGet("detail")]
    [Authorize(Policy = BillingReadPermissions.TaxLevyDetail)]
    public async Task<IResult> DetailAsync([FromQuery] string? period, [FromServices] TaxLevyReader reader, CancellationToken cancellationToken) =>
        period is not null && !TaxLevyReader.IsPeriod(period)
            ? BadPeriod()
            : Results.Ok(await reader.DetailAsync(LegalEntity(), period, cancellationToken).ConfigureAwait(false));

    private IResult BadPeriod() =>
        HttpResults.Problem(new DomainError(ErrorCode.For(ModuleCode.BIL, PlatformErrors.Validation), "period must be yyyy-MM."), HttpContext);

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}

/// <summary>
/// Inspection lists of the records the migration imported (not in contracts/openapi; allow-listed in RouteConformanceAllowList.txt).
/// TODO(hardening): add these operations to bil.yaml and regenerate the contracts.
/// </summary>
[ApiController]
[Route("api/bil/v1/migrated-records")]
internal sealed class MigratedRecordsController : ControllerBase
{
    /// <summary>Opening receivables imported from legacy invoices; <c>search</c> is a prefix of the legacy invoice or policy number.</summary>
    [HttpGet("receivables")]
    [Authorize(Policy = BillingReadPermissions.MigratedRecordList)]
    public async Task<IResult> ReceivablesAsync(
        [FromQuery] Guid? batchId, [FromQuery] Guid? billingAccountId, [FromQuery] string? search, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] MigratedRecordsReader reader, CancellationToken cancellationToken) =>
        Results.Ok(await reader.ReceivablesAsync(LegalEntity(), batchId, billingAccountId, Search(search), cursor, limit, cancellationToken).ConfigureAwait(false));

    /// <summary>SEPA mandates imported with their original reference; <c>search</c> is a prefix of the mandate reference.</summary>
    [HttpGet("mandates")]
    [Authorize(Policy = BillingReadPermissions.MigratedRecordList)]
    public async Task<IResult> MandatesAsync(
        [FromQuery] Guid? batchId, [FromQuery] Guid? billingAccountId, [FromQuery] string? search, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] MigratedRecordsReader reader, CancellationToken cancellationToken) =>
        Results.Ok(await reader.MandatesAsync(LegalEntity(), batchId, billingAccountId, Search(search), cursor, limit, cancellationToken).ConfigureAwait(false));

    // Prefix match only; LIKE wildcards in the term are escaped by EF's StartsWith translation. Overlong terms are cut.
    private static string? Search(string? search) => search is { Length: > 64 } ? search[..64] : search;

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}
