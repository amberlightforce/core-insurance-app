using CoreIns.Modules.Party.Domain;
using System.Text.Json.Nodes;
using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Queries;
using CoreIns.Modules.Party.Services.Screening;
using CoreIns.Platform.Approvals;
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
/// Sanctions screening (REQ-PTY-006, D-SL2-05, BO-PTY-07). The screening rules, the case queue and the maker-checker decision are PTY's; the sanctions
/// list is behind <see cref="IScreeningProvider"/>. Outside Production the only provider is the <see cref="DemoScreeningProvider"/> (every subject Clear
/// with the marker <see cref="StubListVersion"/> in <c>listVersions</c>, except a name with the demo hit token), and it refuses to be registered or
/// constructed in Production. In Production the contract fails closed with PTY-ERR-NOT-AVAILABLE until a vendor adapter registers an
/// <see cref="IScreeningProvider"/> next to <see cref="AddPartyScreeningEngine"/>.
/// </summary>
public static class PartyScreening
{
    /// <summary>The list version a stub result carries, so no caller can mistake it for a real screening.</summary>
    public const string StubListVersion = "STUB-NO-SANCTIONS-LIST";

    /// <summary>Binds <see cref="IPartyScreeningService"/>: the demo provider outside Production, a fail-closed service in Production.</summary>
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

    /// <summary>Registers the engine with the demo provider; throws in Production (D-SL2-05: never bound in Production).</summary>
    public static IServiceCollection AddStubPartyScreening(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        StubGuard.RefuseIn(environment);
        services.AddScoped<IScreeningProvider, DemoScreeningProvider>();
        return services.AddPartyScreeningEngine();
    }

    /// <summary>
    /// Registers pty.Screening.screen and bulk over the registered <see cref="IScreeningProvider"/>, and binds <see cref="IPartyScreeningService"/>
    /// to them. A vendor adapter registers its provider next to this call.
    /// </summary>
    public static IServiceCollection AddPartyScreeningEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ScreeningEngine>();
        services.AddScoped<IValidator<ScreenParty>, ScreenPartyValidator>();
        services.AddScoped<IValidator<BulkScreen>, BulkScreenValidator>();
        services.AddCommandAuditor<ScreenParty, ScreeningScreenResponse, ScreenPartyAuditor>();
        services.AddCommandAuditor<BulkScreen, ScreeningBulkResponse, BulkScreenAuditor>();
        services.AddCommand<ScreenParty, ScreeningScreenResponse, ScreenPartyHandler>(CommandDescriptor.For("pty.Screening.screen"));
        services.AddCommand<BulkScreen, ScreeningBulkResponse, BulkScreenHandler>(CommandDescriptor.For("pty.Screening.bulk"));
        services.AddScoped<IPartyScreeningService, PartyScreeningService>();
        return services;
    }

    /// <summary>The case queue and the maker-checker decision (always registered: cases outlive the provider).</summary>
    public static IServiceCollection AddPartyScreeningCases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOwnerDecidedApprovalType(PartyAuthorityTypes.ScreeningDecisionApprovalType);
        services.AddScoped<ScreeningWorkTasks>();
        services.AddScoped<ScreeningCaseQueries>();
        services.AddScoped<IValidator<DecideScreeningCase>, DecideScreeningCaseValidator>();
        services.AddCommandAuditor<DecideScreeningCase, ScreeningCaseDecideResponse, DecideScreeningCaseAuditor>();
        services.AddCommand<DecideScreeningCase, ScreeningCaseDecideResponse, DecideScreeningCaseHandler>(CommandDescriptor.For("pty.Screening.caseDecide"));
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

/// <summary><c>pty.Screening.screen</c> (audited like every command; no personal data in the audit facts).</summary>
internal sealed record ScreenParty(ScreeningScreenRequest Request) : ICommand<ScreeningScreenResponse>;

/// <summary><c>pty.Screening.bulk</c>: screens existing parties; every hit opens a case.</summary>
internal sealed record BulkScreen(ScreeningBulkRequest Request) : ICommand<ScreeningBulkResponse>;

internal sealed class ScreenPartyValidator : AbstractValidator<ScreenParty>
{
    public ScreenPartyValidator()
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

internal sealed class BulkScreenValidator : AbstractValidator<BulkScreen>
{
    public const int MaxParties = 500;

    public BulkScreenValidator()
    {
        RuleFor(c => c.Request.PartyIds).NotEmpty().Must(ids => ids.Count <= MaxParties).WithMessage($"At most {MaxParties} parties per call.");
        RuleFor(c => c.Request.CallerRef).NotNull();
    }
}

internal sealed class ScreenPartyHandler(ScreeningEngine engine) : ICommandHandler<ScreenParty, ScreeningScreenResponse>
{
    public Task<Result<ScreeningScreenResponse>> HandleAsync(ScreenParty command, CancellationToken cancellationToken) =>
        engine.ScreenAsync(command.Request, cancellationToken);
}

internal sealed class BulkScreenHandler(ScreeningEngine engine) : ICommandHandler<BulkScreen, ScreeningBulkResponse>
{
    public async Task<Result<ScreeningBulkResponse>> HandleAsync(BulkScreen command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        List<ScreeningBulkResponse.ResultItem> items = [];
        foreach (var partyId in request.PartyIds.Distinct())
        {
            var one = await engine.ScreenAsync(new ScreeningScreenRequest { PartyId = partyId, CallerRef = request.CallerRef }, cancellationToken).ConfigureAwait(false);
            if (one.IsFailure)
            {
                if (one.Error.Code.Value == "PTY-ERR-NOT-AVAILABLE")
                {
                    return one.Error;
                }

                items.Add(new ScreeningBulkResponse.ResultItem { PartyId = partyId, PaymentBlock = true, Error = one.Error.Code.Value });
                continue;
            }

            var response = one.Value;
            items.Add(new ScreeningBulkResponse.ResultItem
            {
                PartyId = partyId,
                Result = Enum.Parse<ScreeningBulkResponse.ResultItem.ResultValue>(response.Result.ToString()),
                CaseNumber = response.CaseNumber,
                PaymentBlock = response.PaymentBlock,
            });
        }

        return new ScreeningBulkResponse
        {
            Screened = items.Count,
            Clear = items.Count(i => i.Result == ScreeningBulkResponse.ResultItem.ResultValue.Clear),
            Hits = items.Count(i => i.Result is ScreeningBulkResponse.ResultItem.ResultValue.PotentialHit or ScreeningBulkResponse.ResultItem.ResultValue.Blocked),
            Results = items,
        };
    }
}

internal sealed class ScreenPartyAuditor : ICommandAuditor<ScreenParty, ScreeningScreenResponse>
{
    // The caller's object and the outcome only: never the ad-hoc payee's name, birth date or identifiers (P2).
    public CommandAuditFacts Describe(ScreenParty command, Result<ScreeningScreenResponse>? result) => new()
    {
        ObjectRef = command.Request?.PartyId is { } partyId ? ObjectRef.For(ModuleCode.PTY, "Party", partyId) : command.Request?.CallerRef,
        Changes = result is { IsSuccess: true } ok
            ? [new AuditChange("screeningResult", null, JsonValue.Create($"{ok.Value.Result}:{string.Join(',', ok.Value.ListVersions)}"))]
            : [],
        BusinessKeys = command.Request?.PartyId is { } id ? BusinessKeys.Empty.With("partyId", id.Value.ToString()) : BusinessKeys.Empty,
    };
}

internal sealed class BulkScreenAuditor : ICommandAuditor<BulkScreen, ScreeningBulkResponse>
{
    public CommandAuditFacts Describe(BulkScreen command, Result<ScreeningBulkResponse>? result) => new()
    {
        ObjectRef = command.Request?.CallerRef,
        Changes = result is { IsSuccess: true } ok
            ? [new AuditChange("screeningBulk", null, JsonValue.Create($"screened={ok.Value.Screened};hits={ok.Value.Hits}"))]
            : [],
    };
}

/// <summary>The in-process contract: screen and bulk screen through the command pipeline.</summary>
internal sealed class PartyScreeningService(
    RequestContext context, ICommandHandler<ScreenParty, ScreeningScreenResponse> screen, ICommandHandler<BulkScreen, ScreeningBulkResponse> bulk) : IPartyScreeningService
{
    public async Task<ScreeningScreenResponse> ScreenAsync(ScreeningScreenRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await screen.HandleAsync(new ScreenParty(request), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error);
        }
    }

    public async Task<ScreeningBulkResponse> BulkAsync(ScreeningBulkRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            var result = await bulk.HandleAsync(new BulkScreen(request), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : throw new DomainException(result.Error);
        }
    }
}

/// <summary>Production binding until a vendor adapter is connected: every call fails closed.</summary>
internal sealed class UnavailablePartyScreeningService : IPartyScreeningService
{
    public Task<ScreeningScreenResponse> ScreenAsync(ScreeningScreenRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw new DomainException(DomainError.Of(ModuleCode.PTY, "NOT-AVAILABLE", "Sanctions screening is not connected in this environment; the payment cannot be screened."));

    public Task<ScreeningBulkResponse> BulkAsync(ScreeningBulkRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw new DomainException(DomainError.Of(ModuleCode.PTY, "NOT-AVAILABLE", "Sanctions screening is not connected in this environment; the parties cannot be screened."));
}
