using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Api;

/// <summary>
/// Reads for the billing operations screens (NX-UI-14c): plan instances. Not in
/// contracts/openapi (the contract types them loosely; allow-listed in RouteConformanceAllowList.txt).
/// TODO(hardening): add these operations to bil.yaml and regenerate the contracts.
/// </summary>
[ApiController]
[Route("api/bil/v1/billing-ops")]
internal sealed class BillingOpsController : ControllerBase
{
    /// <summary>Plan instances of attached policy terms; <c>search</c> is a policy-number prefix.</summary>
    [HttpGet("plan-instances")]
    [Authorize(Policy = "bil.PlanInstance.list")]
    public async Task<IResult> PlanInstancesAsync(
        [FromQuery] Guid? billingAccountId, [FromQuery] string? search, [FromQuery] string? cursor, [FromQuery] int? limit,
        [FromServices] BillingOpsReader reader, CancellationToken cancellationToken) =>
        Results.Ok(await reader.PlanInstancesAsync(LegalEntity(), billingAccountId, search is { Length: > 64 } ? search[..64] : search, cursor, limit, cancellationToken).ConfigureAwait(false));

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}
