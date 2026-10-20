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

namespace CoreIns.Modules.Billing.Api;

/// <summary>Permission names of the delinquency operations (<c>x-permission</c>), granted to roles in <c>Platform:Permissions</c>.</summary>
internal static class DelinquencyPermissions
{
    public const string Get = "bil.Delinquency.get";
    public const string List = "bil.Delinquency.list";
    public const string RecordContact = "bil.Delinquency.recordContact";
    public const string Arrange = "bil.Delinquency.arrange";
    public const string ReleaseHold = "bil.Delinquency.releaseHold";
}

/// <summary>REST facade of the non-payment (delinquency) cases (contracts/openapi/bil.yaml, SL6-BIL-DELINQ).</summary>
[ApiController]
[Route("api/bil/v1/delinquencies")]
internal sealed class DelinquencyController : ControllerBase
{
    /// <summary>bil.Delinquency.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = DelinquencyPermissions.Get)]
    public async Task<IResult> GetAsync(string id, [FromServices] DelinquencyReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid) ? await reader.GetAsync(LegalEntity(), guid, cancellationToken).ConfigureAwait(false) : null;
        return found is null ? HttpResults.Problem(BillingErrors.NotFound("delinquency case"), HttpContext) : Results.Ok(new DelinquencyGetResponse { Process = found });
    }

    /// <summary>bil.Delinquency.list by account, policy, state or trigger (codes are upper-snake).</summary>
    [HttpGet]
    [Authorize(Policy = DelinquencyPermissions.List)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] Guid? billingAccountId, [FromQuery] string? state, [FromQuery] Guid? policyId,
        [FromQuery] string? trigger, [FromServices] DelinquencyReader reader, CancellationToken cancellationToken)
    {
        if (!FilterCode.IsValid(state) || !FilterCode.IsValid(trigger))
        {
            return HttpResults.Problem(new DomainError(ErrorCode.For(ModuleCode.BIL, PlatformErrors.Validation), "state and trigger must be upper-snake codes."), HttpContext);
        }

        return Results.Ok(await reader.ListAsync(
            LegalEntity(), billingAccountId is { } a ? new BillingAccountId(a) : null, state, cursor, limit, cancellationToken, policyId, trigger).ConfigureAwait(false));
    }

    /// <summary>bil.Delinquency.recordContact.</summary>
    [HttpPost("record-contact")]
    [Authorize(Policy = DelinquencyPermissions.RecordContact)]
    public async Task<IResult> RecordContactAsync(
        [FromBody] DelinquencyRecordContactRequest request, [FromServices] ICommandHandler<RecordDelinquencyContact, DelinquencyRecordContactResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordDelinquencyContact(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Delinquency.arrange (promise to pay; pauses the clock).</summary>
    [HttpPost("arrange")]
    [Authorize(Policy = DelinquencyPermissions.Arrange)]
    public async Task<IResult> ArrangeAsync(
        [FromBody] DelinquencyArrangeRequest request, [FromServices] ICommandHandler<ArrangeDelinquency, DelinquencyArrangeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ArrangeDelinquency(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>bil.Delinquency.releaseHold (vulnerable customer; pauses the clock, or lifts the hold with <c>release</c>).</summary>
    [HttpPost("release-hold")]
    [Authorize(Policy = DelinquencyPermissions.ReleaseHold)]
    public async Task<IResult> ReleaseHoldAsync(
        [FromBody] DelinquencyReleaseHoldRequest request, [FromServices] ICommandHandler<ReleaseDelinquencyHold, DelinquencyReleaseHoldResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReleaseDelinquencyHold(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    private LegalEntityId LegalEntity()
    {
        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        return services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
    }
}
