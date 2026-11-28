using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Contracts.Api;
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

/// <summary>Permission names of the instrument and mandate operations (<c>x-permission</c>).</summary>
internal static class MandatePermissions
{
    public const string InstrumentAdd = "bil.PaymentInstrument.add";
    public const string InstrumentRemove = "bil.PaymentInstrument.remove";
    public const string InstrumentList = "bil.PaymentInstrument.list";
    public const string Create = "bil.Mandate.create";
    public const string Sign = "bil.Mandate.sign";
    public const string Amend = "bil.Mandate.amend";
    public const string Cancel = "bil.Mandate.cancel";
    public const string Get = "bil.Mandate.get";
}

/// <summary>REST facade of payer instruments and SEPA mandates (BO-BIL-06a, contracts/openapi/bil.yaml). No response carries an IBAN.</summary>
[ApiController]
[Route("api/bil/v1")]
internal sealed class MandateController : ControllerBase
{
    /// <summary>bil.PaymentInstrument.add.</summary>
    [HttpPost("payment-instruments/add")]
    [Authorize(Policy = MandatePermissions.InstrumentAdd)]
    public async Task<IResult> AddInstrumentAsync(
        [FromBody] PaymentInstrumentAddRequest request, [FromServices] ICommandHandler<AddPaymentInstrument, PaymentInstrumentAddResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AddPaymentInstrument(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.PaymentInstrument.remove.</summary>
    [HttpPost("payment-instruments/remove")]
    [Authorize(Policy = MandatePermissions.InstrumentRemove)]
    public async Task<IResult> RemoveInstrumentAsync(
        [FromBody] PaymentInstrumentRemoveRequest request, [FromServices] ICommandHandler<RemovePaymentInstrument, PaymentInstrumentRemoveResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RemovePaymentInstrument(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.PaymentInstrument.list: the instruments of a party, masked.</summary>
    [HttpGet("payment-instruments")]
    [Authorize(Policy = MandatePermissions.InstrumentList)]
    public async Task<IResult> ListInstrumentsAsync(
        [FromQuery] Guid? partyId, [FromQuery] string? cursor, [FromQuery] int? limit, [FromServices] MandateReader reader, CancellationToken cancellationToken) =>
        partyId is null || partyId == Guid.Empty
            ? HttpResults.Problem(DomainError.Of(ModuleCode.BIL, "VALIDATION", "partyId is required."), HttpContext)
            : Results.Ok(await reader.ListInstrumentsAsync(LegalEntity(), new PartyId(partyId.Value), cursor, limit, cancellationToken).ConfigureAwait(false));

    /// <summary>bil.Mandate.create, answered 201 with the mandate as Location.</summary>
    [HttpPost("mandates")]
    [Authorize(Policy = MandatePermissions.Create)]
    public async Task<IResult> CreateAsync(
        [FromBody] MandateCreateRequest request, [FromServices] ICommandHandler<CreateMandate, MandateCreateResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateMandate(request), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Created($"/api/bil/v1/mandates/{result.Value.Mandate.MandateId:D}", result.Value)
            : HttpResults.Problem(result.Error!, HttpContext);
    }

    /// <summary>bil.Mandate.sign.</summary>
    [HttpPost("mandates/sign")]
    [Authorize(Policy = MandatePermissions.Sign)]
    public async Task<IResult> SignAsync(
        [FromBody] MandateSignRequest request, [FromServices] ICommandHandler<SignMandate, MandateSignResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SignMandate(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Mandate.amend.</summary>
    [HttpPost("mandates/amend")]
    [Authorize(Policy = MandatePermissions.Amend)]
    public async Task<IResult> AmendAsync(
        [FromBody] MandateAmendRequest request, [FromServices] ICommandHandler<AmendMandate, MandateAmendResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AmendMandate(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Mandate.cancel.</summary>
    [HttpPost("mandates/cancel")]
    [Authorize(Policy = MandatePermissions.Cancel)]
    public async Task<IResult> CancelAsync(
        [FromBody] MandateCancelRequest request, [FromServices] ICommandHandler<CancelMandate, MandateCancelResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new CancelMandate(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Mandate.get (native and migrated mandates alike).</summary>
    [HttpGet("mandates/{id}")]
    [Authorize(Policy = MandatePermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] MandateReader reader, CancellationToken cancellationToken)
    {
        var mandate = Guid.TryParse(id, out var mandateId) ? await reader.GetAsync(LegalEntity(), mandateId, cancellationToken).ConfigureAwait(false) : null;
        return mandate is null
            ? HttpResults.Problem(BillingErrors.NotFound("mandate"), HttpContext)
            : Results.Ok(new MandateGetResponse { Mandate = mandate });
    }

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}
