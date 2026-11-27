using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Modules.Product.Queries;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Product.Api;

/// <summary>REST facade of the PFC runtime services (BO-PFC-05): availability, describe and policy-draft validation. All are queries sent as POST.</summary>
[ApiController]
[Route("api/pfc/v1")]
internal sealed class RuntimeController : ControllerBase
{
    /// <summary>pfc.Availability.check: the sellable products for a channel and transaction type on <c>validAt</c> (default today).</summary>
    [HttpPost("availability/check")]
    [SkipIdempotency]
    [Authorize(Policy = ProductPermissions.AvailabilityCheck)]
    public async Task<IResult> CheckAsync(
        [FromBody] AvailabilityCheckRequest request, [FromQuery] string? validAt,
        [FromServices] RuntimeQueries queries, [FromServices] IClock clock, CancellationToken cancellationToken) =>
        Date(clock, validAt, out var date)
            ? this.Respond(await queries.CheckAvailabilityAsync(request, date, cancellationToken).ConfigureAwait(false))
            : BadDate();

    /// <summary>pfc.Product.describe: the published version in force (by product) or with this hash, for UIs and channels.</summary>
    [HttpPost("products/describe")]
    [SkipIdempotency]
    [Authorize(Policy = ProductPermissions.ProductDescribe)]
    public async Task<IResult> DescribeAsync(
        [FromBody] ProductDescribeRequest request, [FromQuery] string? validAt,
        [FromServices] RuntimeQueries queries, [FromServices] IClock clock, CancellationToken cancellationToken) =>
        Date(clock, validAt, out var date)
            ? this.Respond(await queries.DescribeAsync(request, date, cancellationToken).ConfigureAwait(false))
            : BadDate();

    /// <summary>pfc.PolicyDraft.validate: findings for a risk and coverage selection (a pure query).</summary>
    [HttpPost("policy-drafts/validate")]
    [SkipIdempotency]
    [Authorize(Policy = ProductPermissions.PolicyDraftValidate)]
    public async Task<IResult> ValidateAsync(
        [FromBody] PolicyDraftValidateRequest request, [FromServices] RuntimeQueries queries, CancellationToken cancellationToken) =>
        this.Respond(await queries.ValidateDraftAsync(request, cancellationToken).ConfigureAwait(false));

    private static bool Date(IClock clock, string? validAt, out BusinessDate date) =>
        ProductVersionsController.TryTime(clock, validAt, null, out date, out _);

    private IResult BadDate() =>
        this.ProblemOf(DomainError.Of(ModuleCode.PFC, "VALIDATION", "validAt must be a date or instant."));
}
