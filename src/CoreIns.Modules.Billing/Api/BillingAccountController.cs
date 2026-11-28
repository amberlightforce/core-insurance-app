using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the account lifecycle operations (<c>x-permission</c>).</summary>
internal static class AccountPermissions
{
    public const string Search = "bil.BillingAccount.search";
    public const string Update = "bil.BillingAccount.update";
    public const string Close = "bil.BillingAccount.close";
    public const string Reopen = "bil.BillingAccount.reopen";
    public const string ChangePayer = "bil.BillingAccount.changePayer";
}

/// <summary>REST facade of the billing account lifecycle (BO-BIL-01, contracts/openapi/bil.yaml). <c>get</c> stays on <see cref="BillingController"/>.</summary>
[ApiController]
[Route("api/bil/v1")]
internal sealed class BillingAccountController : ControllerBase
{
    /// <summary>bil.BillingAccount.search: cursor-paged, filtered by payer, status, currency or account number.</summary>
    [HttpGet("billing-accounts/search")]
    [Authorize(Policy = AccountPermissions.Search)]
    public async Task<IResult> SearchAsync(
        [FromQuery] Guid? payerPartyId, [FromQuery] string? status, [FromQuery] string? currency, [FromQuery] string? accountNumber,
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] BillingAccountSearchReader reader, CancellationToken cancellationToken)
    {
        BillingAccountStatus? parsed = null;
        if (status is not null)
        {
            if (!Enum.TryParse<BillingAccountStatus>(status, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                return Invalid($"The status '{status}' is not a billing account state.");
            }

            parsed = value;
        }

        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)))
        {
            return Invalid("The currency must be an ISO 4217 code in capitals.");
        }

        if (cursor is not null && !Guid.TryParse(cursor, out _))
        {
            return Invalid("Invalid cursor.");
        }

        return Results.Ok(await reader.SearchAsync(
            LegalEntity(), new BillingAccountFilter(payerPartyId is { } p ? new PartyId(p) : null, parsed, currency, accountNumber), cursor, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>bil.BillingAccount.update: suspend or resume.</summary>
    [HttpPatch("billing-accounts/{id}")]
    [Authorize(Policy = AccountPermissions.Update)]
    public async Task<IResult> UpdateAsync(
        string id, [FromBody] BillingAccountUpdateRequest request, [FromServices] ICommandHandler<UpdateBillingAccount, BillingAccountUpdateResponse> handler, CancellationToken cancellationToken) =>
        Guid.TryParse(id, out var guid)
            ? (await handler.HandleAsync(new UpdateBillingAccount(new BillingAccountId(guid), request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext)
            : HttpResults.Problem(BillingErrors.NotFound("billing account"), HttpContext);

    /// <summary>bil.BillingAccount.close.</summary>
    [HttpPost("billing-accounts/close")]
    [Authorize(Policy = AccountPermissions.Close)]
    public async Task<IResult> CloseAsync(
        [FromBody] BillingAccountCloseRequest request, [FromServices] ICommandHandler<CloseBillingAccount, BillingAccountCloseResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CloseBillingAccount(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.BillingAccount.reopen.</summary>
    [HttpPost("billing-accounts/reopen")]
    [Authorize(Policy = AccountPermissions.Reopen)]
    public async Task<IResult> ReopenAsync(
        [FromBody] BillingAccountReopenRequest request, [FromServices] ICommandHandler<ReopenBillingAccount, BillingAccountReopenResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReopenBillingAccount(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.BillingAccount.changePayer.</summary>
    [HttpPost("billing-accounts/change-payer")]
    [Authorize(Policy = AccountPermissions.ChangePayer)]
    public async Task<IResult> ChangePayerAsync(
        [FromBody] BillingAccountChangePayerRequest request, [FromServices] ICommandHandler<ChangeBillingAccountPayer, BillingAccountChangePayerResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ChangeBillingAccountPayer(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private IResult Invalid(string message) => HttpResults.Problem(DomainError.Of(ModuleCode.BIL, "VALIDATION", message), HttpContext);

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}
