using System.Text.Json.Nodes;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CoreIns.Modules.Party.Services;

/// <summary>
/// Sanctions screening for the claims slice (D-SL2-05, REQ-PTY-006 stub): no sanctions list is connected, so outside
/// Production <c>pty.Screening.screen</c> answers <c>Clear</c> with the stub marker <see cref="StubListVersion"/> in
/// <c>listVersions</c>, and the stub refuses to be registered or constructed in Production. In Production the contract
/// fails closed with PTY-ERR-NOT-AVAILABLE until the real screening adapter (W2-PTY-03) exists.
/// </summary>
public static class PartyScreening
{
    /// <summary>The list version a stub result carries, so no caller can mistake it for a real screening.</summary>
    public const string StubListVersion = "STUB-NO-SANCTIONS-LIST";

    /// <summary>Binds <see cref="IPartyScreeningService"/>: the stub outside Production, a fail-closed service in Production.</summary>
    public static IServiceCollection AddPartyScreening(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.IsProduction())
        {
            services.AddScoped<IPartyScreeningService, UnavailablePartyScreeningService>();
            return services;
        }

        return services.AddStubPartyScreening(environment);
    }

    /// <summary>Registers the stub screening; throws in Production (D-SL2-05: never bound in Production).</summary>
    public static IServiceCollection AddStubPartyScreening(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        StubGuard.RefuseIn(environment);
        services.AddScoped<IValidator<ScreenStub>, ScreenStubValidator>();
        services.AddCommandAuditor<ScreenStub, ScreeningScreenResponse, ScreenStubAuditor>();
        services.AddCommand<ScreenStub, ScreeningScreenResponse, ScreenStubHandler>(CommandDescriptor.For("pty.Screening.screen"));
        services.AddScoped<IPartyScreeningService, StubPartyScreeningService>();
        return services;
    }
}

/// <summary>Refuses the stub in Production.</summary>
internal static class StubGuard
{
    public static void RefuseIn(IHostEnvironment environment)
    {
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "The PTY screening stub (D-SL2-05) is never bound in Production: no sanctions list is connected. Configure the real screening adapter.");
        }
    }
}

/// <summary><c>pty.Screening.screen</c> on the stub (audited like every command; no personal data in the audit facts).</summary>
internal sealed record ScreenStub(ScreeningScreenRequest Request) : ICommand<ScreeningScreenResponse>;

internal sealed class ScreenStubValidator : AbstractValidator<ScreenStub>
{
    public ScreenStubValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.CallerRef).NotNull().When(c => c.Request is not null);
    }

    /// <summary>The contract's oneOf (partyId or adHocPayee with a name); a violation is PTY-ERR-SCREEN-INPUT (422, non-retryable).</summary>
    public static DomainError? InputProblem(ScreeningScreenRequest request) =>
        (request.PartyId is null) == (request.AdHocPayee is null)
            ? DomainError.Of(ModuleCode.PTY, "SCREEN-INPUT", "Give exactly one of partyId or adHocPayee.")
            : request.AdHocPayee is { } payee && string.IsNullOrWhiteSpace(payee.Name)
                ? DomainError.Of(ModuleCode.PTY, "SCREEN-INPUT", "An ad-hoc payee needs a name.")
                : null;
}

internal sealed class ScreenStubHandler : ICommandHandler<ScreenStub, ScreeningScreenResponse>
{
    public ScreenStubHandler(IHostEnvironment environment)
    {
        StubGuard.RefuseIn(environment);
    }

    public Task<Result<ScreeningScreenResponse>> HandleAsync(ScreenStub command, CancellationToken cancellationToken)
    {
        if (ScreenStubValidator.InputProblem(command.Request) is { } problem)
        {
            return Task.FromResult<Result<ScreeningScreenResponse>>(problem);
        }

        return Task.FromResult<Result<ScreeningScreenResponse>>(new ScreeningScreenResponse
        {
            Result = ScreeningScreenResponse.ResultValue.Clear,
            ListVersions = [PartyScreening.StubListVersion],
            PaymentBlock = false,
        });
    }
}

internal sealed class ScreenStubAuditor : ICommandAuditor<ScreenStub, ScreeningScreenResponse>
{
    // The caller's object and the outcome only: never the ad-hoc payee's name, birth date or identifiers (P2).
    public CommandAuditFacts Describe(ScreenStub command, Result<ScreeningScreenResponse>? result) => new()
    {
        ObjectRef = command.Request?.PartyId is { } partyId ? ObjectRef.For(ModuleCode.PTY, "Party", partyId) : command.Request?.CallerRef,
        Changes = result is { IsSuccess: true } ok
            ? [new AuditChange("screeningResult", null, JsonValue.Create($"{ok.Value.Result}:{PartyScreening.StubListVersion}"))]
            : [],
        BusinessKeys = command.Request?.PartyId is { } id ? BusinessKeys.Empty.With("partyId", id.Value.ToString()) : BusinessKeys.Empty,
    };
}

/// <summary>The stub's in-process contract: screen through the pipeline; bulk screening is not built.</summary>
internal sealed class StubPartyScreeningService(RequestContext context, ICommandHandler<ScreenStub, ScreeningScreenResponse> screen) : IPartyScreeningService
{
    public async Task<ScreeningScreenResponse> ScreenAsync(ScreeningScreenRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await screen.HandleAsync(new ScreenStub(request), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error);
        }
    }

    public Task<ScreeningBulkResponse> BulkAsync(ScreeningBulkRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Screening.bulk");
}

/// <summary>Production binding until a real sanctions list is connected: every call fails closed.</summary>
internal sealed class UnavailablePartyScreeningService : IPartyScreeningService
{
    public Task<ScreeningScreenResponse> ScreenAsync(ScreeningScreenRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw new DomainException(DomainError.Of(ModuleCode.PTY, "NOT-AVAILABLE", "Sanctions screening is not connected in this environment; the payment cannot be screened."));

    public Task<ScreeningBulkResponse> BulkAsync(ScreeningBulkRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw PartyPartyService.NotAvailable("pty.Screening.bulk");
}
