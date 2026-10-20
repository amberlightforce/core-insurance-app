using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Ai;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace CoreIns.Platform.Ai;

/// <summary>Permission names of the AI control plane (<c>x-permission</c>), granted in <c>Platform:Permissions</c>.</summary>
internal static class AiPermissions
{
    public const string Invoke = "plt.Ai.invoke";
    public const string RecordOutcome = "plt.Ai.recordOutcome";
    public const string KillSwitch = "plt.Ai.killSwitch";
    public const string ToggleSet = "plt.AiToggle.set";
    public const string ToggleResolve = "plt.AiToggle.resolve";
    public const string FlagEvaluate = "plt.FeatureFlag.evaluate";
}

/// <summary>Reads of the control plane: the effective state of a feature and feature flags.</summary>
internal sealed class AiQueries(NpgsqlDataSource dataSource, DbAiGate gate, IConfiguration configuration)
{
    public async Task<AiToggleResolveResponse> ResolveAsync(AiToggleResolveRequest request, CancellationToken cancellationToken)
    {
        var featureId = request.FeatureId.Value;
        var decision = await gate.CheckFreshAsync(featureId, cancellationToken).ConfigureAwait(false);
        bool enabled = false;
        bool killSwitch = false;
        string? status = null;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT COALESCE((SELECT enabled FROM plt.ai_toggle WHERE feature_id = @f), false), "
                + "COALESCE((SELECT bool_or(active) FROM plt.ai_kill_switch WHERE scope IN ('GLOBAL', @f)), false), "
                + "(SELECT register_status FROM plt.ai_feature WHERE feature_id = @f)", connection);
            command.Parameters.AddWithValue("f", featureId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            enabled = reader.GetBoolean(0);
            killSwitch = reader.GetBoolean(1);
            status = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        catch (NpgsqlException)
        {
            // The gate answer above already says StateUnreadable; the details stay at their fail-closed defaults.
        }

        return new AiToggleResolveResponse
        {
            FeatureId = request.FeatureId,
            Enabled = enabled,
            Allowed = decision.IsAllowed,
            Reason = decision.Reason,
            RegisterStatus = status,
            KillSwitchActive = killSwitch,
        };
    }

    public async Task<FeatureFlagEvaluateResponse> EvaluateFlagAsync(FeatureFlagEvaluateRequest request, CancellationToken cancellationToken)
    {
        if (AiScopes.IsFeature(request.Flag))
        {
            var decision = await gate.CheckAsync(request.Flag, cancellationToken).ConfigureAwait(false);
            return new FeatureFlagEvaluateResponse { State = decision.IsAllowed, Reason = decision.Reason };
        }

        // TODO(hardening): flags by tenant / legal entity / role (plt.feature_flag table); only a global configured boolean for now.
        var configured = configuration.GetSection("Platform:FeatureFlags")[request.Flag];
        return bool.TryParse(configured, out var state)
            ? new FeatureFlagEvaluateResponse { State = state }
            : new FeatureFlagEvaluateResponse { State = false, Reason = "NOT_CONFIGURED" };
    }
}

/// <summary>REST facade of the AI control plane (contracts/openapi/plt.yaml).</summary>
[ApiController]
[Route("api/plt/v1")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "MVC actions are instance methods.")]
internal sealed class AiController : ControllerBase
{
    [HttpPost("ai-toggle/set")]
    [Authorize(Policy = AiPermissions.ToggleSet)]
    public async Task<IResult> SetAsync([FromBody] AiToggleSetRequest body, [FromServices] ICommandHandler<SetAiToggle, AiToggleSetResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new SetAiToggle(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [SkipIdempotency]
    [HttpPost("ai-toggle/resolve")]
    [Authorize(Policy = AiPermissions.ToggleResolve)]
    public async Task<IResult> ResolveAsync([FromBody] AiToggleResolveRequest body, [FromServices] AiQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.ResolveAsync(body, cancellationToken).ConfigureAwait(false));

    [HttpPost("ai/kill-switch")]
    [Authorize(Policy = AiPermissions.KillSwitch)]
    public async Task<IResult> KillSwitchAsync([FromBody] AiKillSwitchRequest body, [FromServices] ICommandHandler<AiKillSwitch, AiKillSwitchResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AiKillSwitch(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("ai/invoke")]
    [Authorize(Policy = AiPermissions.Invoke)]
    public async Task<IResult> InvokeAsync([FromBody] AiInvokeRequest body, [FromServices] ICommandHandler<InvokeAi, AiInvokeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new InvokeAi(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [HttpPost("ai/record-outcome")]
    [Authorize(Policy = AiPermissions.RecordOutcome)]
    public async Task<IResult> RecordOutcomeAsync([FromBody] AiRecordOutcomeRequest body, [FromServices] ICommandHandler<RecordAiOutcome, AiRecordOutcomeResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RecordAiOutcome(body), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    [SkipIdempotency]
    [HttpPost("feature-flags/evaluate")]
    [Authorize(Policy = AiPermissions.FlagEvaluate)]
    public async Task<IResult> EvaluateFlagAsync([FromBody] FeatureFlagEvaluateRequest body, [FromServices] AiQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.EvaluateFlagAsync(body, cancellationToken).ConfigureAwait(false));
}

/// <summary>In-process contracts of the control plane (<c>IPlatformAiService</c> and friends) for the modules.</summary>
internal sealed class PlatformAiService(
    RequestContext context, ICommandHandler<InvokeAi, AiInvokeResponse> invoke, ICommandHandler<RecordAiOutcome, AiRecordOutcomeResponse> record)
    : IPlatformAiService
{
    public async Task<AiInvokeResponse> InvokeAsync(AiInvokeRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await invoke.HandleAsync(new InvokeAi(request), cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<AiRecordOutcomeResponse> RecordOutcomeAsync(AiRecordOutcomeRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await record.HandleAsync(new RecordAiOutcome(request), cancellationToken).ConfigureAwait(false));
        }
    }

    private static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new DomainException(result.Error);
}

internal sealed class PlatformAiToggleService(AiQueries queries) : IPlatformAiToggleService
{
    public Task<AiToggleResolveResponse> ResolveAsync(AiToggleResolveRequest request, CancellationToken cancellationToken = default) =>
        queries.ResolveAsync(request, cancellationToken);
}

internal sealed class PlatformFeatureFlagService(AiQueries queries) : IPlatformFeatureFlagService
{
    public Task<FeatureFlagEvaluateResponse> EvaluateAsync(FeatureFlagEvaluateRequest request, CancellationToken cancellationToken = default) =>
        queries.EvaluateFlagAsync(request, cancellationToken);
}

/// <summary>Payload of <c>cmp.AiSystemStatusChanged</c> as far as the control plane reads it (Platform does not reference CMP).</summary>
internal sealed record AiSystemStatusPayload(string FeatureId, string NewStatus);

/// <summary>Payload of <c>dat.BiasThresholdBreached</c> as far as the control plane reads it.</summary>
internal sealed record BiasBreachPayload(string? FeatureId);

/// <summary>CMP register status changed: anything but APPROVED switches that feature off (REQ-PLT-354); other features are untouched.</summary>
internal sealed class AiSystemStatusChangedHandler(AiControlPlane plane) : IEventHandler<AiSystemStatusPayload>
{
    public async Task HandleAsync(EventEnvelope envelope, AiSystemStatusPayload payload, CancellationToken cancellationToken)
    {
        if (AiScopes.IsFeature(payload.FeatureId) && !string.IsNullOrEmpty(payload.NewStatus))
        {
            await plane.ApplyRegisterStatusAsync(payload.FeatureId, payload.NewStatus, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>DAT bias threshold breach on an AI feature: that feature is switched off.</summary>
internal sealed class BiasThresholdBreachedHandler(AiControlPlane plane) : IEventHandler<BiasBreachPayload>
{
    public async Task HandleAsync(EventEnvelope envelope, BiasBreachPayload payload, CancellationToken cancellationToken)
    {
        // A breach on a rating artefact (no AI feature id) is not the control plane's business.
        if (payload.FeatureId is { } featureId && AiScopes.IsFeature(featureId))
        {
            await plane.DisableAsync(featureId, AiChangeReasons.BiasThreshold, "Bias threshold breached", cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Drops the gate cache of this process when a toggle or kill switch event arrives (the 5 s TTL covers other processes).</summary>
internal sealed class AiGateInvalidationHandler(DbAiGate gate) : IEventHandler<System.Text.Json.Nodes.JsonObject>
{
    public Task HandleAsync(EventEnvelope envelope, System.Text.Json.Nodes.JsonObject payload, CancellationToken cancellationToken)
    {
        gate.Invalidate();
        return Task.CompletedTask;
    }
}

/// <summary>Registration of the AI control plane in the Platform module.</summary>
internal static class AiRegistration
{
    public static IServiceCollection AddAiControlPlane(this IServiceCollection services)
    {
        services.AddOptions<AiGateOptions>().BindConfiguration(AiGateOptions.Section);
        services.TryAddSingleton<DbAiGate>();
        services.TryAddSingleton<IAiGate>(sp => sp.GetRequiredService<DbAiGate>());
        services.TryAddScoped<IAiRegisterLookup, DenyAllAiRegisterLookup>();
        services.TryAddScoped<IAiProvider, FakeAiProvider>();
        services.TryAddScoped<AiControlPlane>();
        services.TryAddScoped<AiQueries>();

        services.AddScoped<IValidator<AiKillSwitch>, AiKillSwitchValidator>();
        services.AddScoped<IValidator<InvokeAi>, InvokeAiValidator>();
        services.AddCommandAuditor<SetAiToggle, AiToggleSetResponse, SetAiToggleAuditor>();
        services.AddCommand<SetAiToggle, AiToggleSetResponse, SetAiToggleHandler>(CommandDescriptor.For("plt.AiToggle.set"));
        services.AddCommandAuditor<AiKillSwitch, AiKillSwitchResponse, AiKillSwitchAuditor>();
        services.AddCommand<AiKillSwitch, AiKillSwitchResponse, AiKillSwitchHandler>(CommandDescriptor.For("plt.Ai.killSwitch"));
        services.AddCommandAuditor<InvokeAi, AiInvokeResponse, InvokeAiAuditor>();
        services.AddCommand<InvokeAi, AiInvokeResponse, InvokeAiHandler>(CommandDescriptor.For("plt.Ai.invoke"));
        services.AddCommandAuditor<RecordAiOutcome, AiRecordOutcomeResponse, RecordAiOutcomeAuditor>();
        services.AddCommand<RecordAiOutcome, AiRecordOutcomeResponse, RecordAiOutcomeHandler>(CommandDescriptor.For("plt.Ai.recordOutcome"));

        services.TryAddScoped<IPlatformAiService, PlatformAiService>();
        services.TryAddScoped<IPlatformAiToggleService, PlatformAiToggleService>();
        services.TryAddScoped<IPlatformFeatureFlagService, PlatformFeatureFlagService>();

        services.AddEventHandler<AiSystemStatusPayload, AiSystemStatusChangedHandler>(
            new EventDescriptor(ModuleCode.CMP, EventTypeName.Parse("AiSystemStatusChanged"), "1.0", ["AiFeature"], DataClassification.P0),
            "PLT.AiSystemStatusChanged.DisableFeature", ModuleCode.PLT);
        services.AddEventHandler<BiasBreachPayload, BiasThresholdBreachedHandler>(
            new EventDescriptor(ModuleCode.DAT, EventTypeName.Parse("BiasThresholdBreached"), "1.0", ["Model", "RatingArtefact"], DataClassification.P0),
            "PLT.BiasThresholdBreached.DisableFeature", ModuleCode.PLT);
        services.AddEventHandler<System.Text.Json.Nodes.JsonObject, AiGateInvalidationHandler>(
            EventDescriptor.From(AiToggleChangedV1.Descriptor), "PLT.AiToggleChanged.InvalidateGate", ModuleCode.PLT);
        services.AddEventHandler<System.Text.Json.Nodes.JsonObject, AiGateInvalidationHandler>(
            EventDescriptor.From(AiKillSwitchActivatedV1.Descriptor), "PLT.AiKillSwitchActivated.InvalidateGate", ModuleCode.PLT);

        services.AddErrorDefinitions(
            ErrorDefinition.For(ModuleCode.PLT, AiErrors.Disabled, 503, "Η λειτουργία τεχνητής νοημοσύνης δεν είναι διαθέσιμη", "The AI feature is not available", retryable: true)
                .Describe("Η λειτουργία έχει απενεργοποιηθεί (διακόπτης έκτακτης ανάγκης ή ρύθμιση). Συνεχίστε με τη χειροκίνητη διαδικασία.",
                    "The feature is switched off (kill switch or toggle). Continue on the manual path; nothing was changed."),
            ErrorDefinition.For(ModuleCode.PLT, AiErrors.NotRegistered, 422, "Η λειτουργία δεν είναι εγκεκριμένη στο μητρώο ΤΝ", "The feature is not approved in the AI register")
                .Describe("Μόνο λειτουργίες με κατάσταση APPROVED στο μητρώο ΤΝ μπορούν να ενεργοποιηθούν.",
                    "Only features with register status APPROVED can be enabled."));
        return services;
    }
}
