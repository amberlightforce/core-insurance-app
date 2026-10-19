using CoreIns.Modules.Market.Contracts.Events;
using CoreIns.Modules.Policy.Commands.PackRollback;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Events;
using CoreIns.Modules.Policy.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Policy.Api;

/// <summary>REST facade of <c>pol.PackRollbackException.*</c> (contracts/openapi/pol.yaml): thin.</summary>
[ApiController]
[Route("api/pol/v1/pack-rollback-exceptions")]
internal sealed class PackRollbackController : ControllerBase
{
    public const string ListPermission = "pol.PackRollbackException.list";
    public const string GetPermission = "pol.PackRollbackException.get";
    public const string ReviewPermission = "pol.PackRollbackException.review";

    /// <summary>pol.PackRollbackException.list.</summary>
    [HttpGet]
    [Authorize(Policy = ListPermission)]
    public async Task<IResult> ListAsync(
        [FromQuery] string? status, [FromQuery] string? activationId, [FromQuery] int? limit, [FromQuery] string? cursor,
        [FromServices] PackRollbackReader reader, CancellationToken cancellationToken) =>
        (await reader.ListAsync(status, activationId, limit, cursor, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.PackRollbackException.get.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = GetPermission)]
    public async Task<IResult> GetAsync(string id, [FromServices] PackRollbackReader reader, CancellationToken cancellationToken) =>
        (await reader.GetAsync(id, cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>pol.PackRollbackException.review.</summary>
    [HttpPost("review")]
    [Authorize(Policy = ReviewPermission)]
    public async Task<IResult> ReviewAsync(
        [FromBody] PackRollbackExceptionReviewRequest request, [FromServices] ICommandHandler<ReviewPackRollbackException, PackRollbackExceptionView> handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ReviewPackRollbackException(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}

/// <summary>DI of the pack-rollback consumer, queue and review (SL5-POL-ROLLBACK).</summary>
internal static class PackRollbackRegistration
{
    public static IServiceCollection AddPackRollback(this IServiceCollection services)
    {
        services.AddScoped<PackRollbackReader>();
        services.AddScoped<IValidator<ReviewPackRollbackException>, ReviewPackRollbackExceptionValidator>();
        services.AddCommandAuditor<ReviewPackRollbackException, PackRollbackExceptionView, ReviewPackRollbackExceptionAuditor>();
        services.AddCommand<ReviewPackRollbackException, PackRollbackExceptionView, ReviewPackRollbackExceptionHandler>(
            CommandDescriptor.For("pol.PackRollbackException.review"));
        services.AddEventHandler<PackRolledBackV1, PackRolledBackHandler>(EventDescriptor.From(PackRolledBackV1.Descriptor), PackRolledBackHandler.Name, ModuleCode.POL);
        return services;
    }
}
