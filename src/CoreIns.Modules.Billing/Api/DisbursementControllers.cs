using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the payee-account and disbursement operations (<c>x-permission</c>).</summary>
internal static class DisbursementPermissions
{
    public const string PayeeAccountCreate = "bil.PayeeAccount.create";
    public const string PayeeAccountGet = "bil.PayeeAccount.get";
    public const string DisbursementGet = "bil.Disbursement.get";
}

/// <summary>
/// REST facade of payee accounts and disbursements (SL2-BIL-DISB). The IBAN travels only in the create request body,
/// never in a path or query (D-SLC-05), and responses carry the masked IBAN. <c>bil.Disbursement.request</c> has no
/// REST endpoint in the slice: sources call it in process after their own approval (CLM transaction-set approval).
/// </summary>
[ApiController]
[Route("api/bil/v1")]
internal sealed class DisbursementController : ControllerBase
{
    /// <summary>bil.PayeeAccount.create → 201, Location = the account.</summary>
    [HttpPost("payee-accounts")]
    [Authorize(Policy = DisbursementPermissions.PayeeAccountCreate)]
    public async Task<IResult> CreatePayeeAccountAsync(
        [FromBody] PayeeAccountCreateRequest request, [FromServices] ICommandHandler<CreatePayeeAccount, PayeeAccountCreateResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreatePayeeAccount(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/bil/v1/payee-accounts/{result.Value.PayeeAccountId:D}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>bil.PayeeAccount.get (masked IBAN), optionally as valid on <c>validAt</c> and for a purpose.</summary>
    [HttpGet("payee-accounts/{id}")]
    [Authorize(Policy = DisbursementPermissions.PayeeAccountGet)]
    public async Task<IResult> GetPayeeAccountAsync(
        string id, [FromQuery] string? validAt, [FromQuery] string? purpose, [FromServices] DisbursementReader reader,
        [FromServices] IOptions<BillingOptions> options, CancellationToken cancellationToken)
    {
        BusinessDate? validOn = null;
        if (validAt is not null)
        {
            if (BusinessDate.TryParse(validAt, out var date))
            {
                validOn = date;
            }
            else if (Instant.TryParse(validAt, out var instant))
            {
                validOn = instant.ToBusinessDate(options.Value.Zone);
            }
            else
            {
                return HttpResults.Problem(new DomainError(ErrorCode.For(ModuleCode.BIL, PlatformErrors.Validation), "validAt must be a date or an RFC 3339 instant."), HttpContext);
            }
        }

        var found = Guid.TryParse(id, out var guid) ? await reader.PayeeAccountAsync(LegalEntity(), guid, validOn, cancellationToken).ConfigureAwait(false) : null;
        return found is not null && (purpose is null || found.Purpose == purpose)
            ? Results.Ok(found)
            : HttpResults.Problem(BillingErrors.NotFound("payee account"), HttpContext);
    }

    /// <summary>bil.Disbursement.get: state, dates, method and masked payee account (REQ-BIL-213).</summary>
    [HttpGet("disbursements/{id}")]
    [Authorize(Policy = DisbursementPermissions.DisbursementGet)]
    public async Task<IResult> GetDisbursementAsync(string id, [FromServices] DisbursementReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid)
            ? await reader.DisbursementAsync(LegalEntity(), new DisbursementId(guid), cancellationToken).ConfigureAwait(false)
            : null;
        return found is null
            ? HttpResults.Problem(BillingErrors.NotFound("disbursement"), HttpContext)
            : Results.Ok(new DisbursementGetResponse { Disbursement = found });
    }

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}
