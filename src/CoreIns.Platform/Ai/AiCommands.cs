using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Ai;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Ai;

/// <summary>plt.AiToggle.set: enabling needs register status APPROVED (D-SL6-08); disabling is always allowed.</summary>
internal sealed record SetAiToggle(AiToggleSetRequest Request) : ICommand<AiToggleSetResponse>;

internal sealed class SetAiToggleHandler(AiControlPlane plane, IAiRegisterLookup register, RequestContext context)
    : ICommandHandler<SetAiToggle, AiToggleSetResponse>
{
    public async Task<Result<AiToggleSetResponse>> HandleAsync(SetAiToggle command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var featureId = request.FeatureId.Value;
        if (request.Enabled)
        {
            var status = await register.GetStatusAsync(featureId, cancellationToken).ConfigureAwait(false);
            if (status != AiRegisterStatuses.Approved)
            {
                return DomainError.Of(ModuleCode.PLT, AiErrors.NotRegistered,
                    status is null ? "The feature is not in the AI register." : $"The feature has register status {status}; only APPROVED features can be enabled.");
            }
        }

        var (_, version) = await plane.SetToggleAsync(featureId, request.Enabled, context.Actor, AiChangeReasons.Manual, request.Reason, cancellationToken).ConfigureAwait(false);
        return new AiToggleSetResponse { FeatureId = request.FeatureId, Enabled = request.Enabled, Version = checked((int)version) };
    }
}

internal sealed class SetAiToggleAuditor : ICommandAuditor<SetAiToggle, AiToggleSetResponse>
{
    public CommandAuditFacts Describe(SetAiToggle command, Result<AiToggleSetResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.PLT, "AiFeature", command.Request.FeatureId.Value),
        Changes = result is { IsSuccess: true } ? [new AuditChange("enabled", null, JsonValue.Create(command.Request.Enabled))] : [],
        BusinessKeys = BusinessKeys.Empty.With("featureId", command.Request.FeatureId.Value),
    };
}

/// <summary>plt.Ai.killSwitch: activates or clears the global or a per-feature kill switch.</summary>
internal sealed record AiKillSwitch(AiKillSwitchRequest Request) : ICommand<AiKillSwitchResponse>;

internal sealed class AiKillSwitchValidator : AbstractValidator<AiKillSwitch>
{
    public AiKillSwitchValidator() =>
        RuleFor(c => c.Request.Scope).Must(AiScopes.IsValid).WithMessage("scope must be GLOBAL or an AI feature id (AI-XXX-NN).");
}

internal sealed class AiKillSwitchHandler(AiControlPlane plane, RequestContext context) : ICommandHandler<AiKillSwitch, AiKillSwitchResponse>
{
    public async Task<Result<AiKillSwitchResponse>> HandleAsync(AiKillSwitch command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var active = request.Action == AiKillSwitchRequest.ActionValue.Activate;
        await plane.SetKillSwitchAsync(request.Scope, active, context.Actor, request.Reason, cancellationToken).ConfigureAwait(false);
        return new AiKillSwitchResponse { Scope = request.Scope, Active = active };
    }
}

internal sealed class AiKillSwitchAuditor : ICommandAuditor<AiKillSwitch, AiKillSwitchResponse>
{
    public CommandAuditFacts Describe(AiKillSwitch command, Result<AiKillSwitchResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.PLT, command.Request.Scope == AiScopes.Global ? "AiGlobal" : "AiFeature", command.Request.Scope),
        Changes = result is { IsSuccess: true } ? [new AuditChange("killSwitch", null, JsonValue.Create(command.Request.Action.ToString()))] : [],
    };
}

/// <summary>
/// plt.Ai.invoke: gate before and after the provider call. When the gate closes meanwhile the output is discarded, the
/// inputs stay in the interaction row (written outside the command transaction, which rolls back) and the caller gets 503.
/// </summary>
internal sealed record InvokeAi(AiInvokeRequest Request) : ICommand<AiInvokeResponse>;

internal sealed class InvokeAiValidator : AbstractValidator<InvokeAi>
{
    public const int MaxInputBytes = 32 * 1024;

    public InvokeAiValidator() =>
        RuleFor(c => c.Request.Inputs)
            .Must(i => i.ValueKind == JsonValueKind.Object && i.GetRawText().Length <= MaxInputBytes)
            .WithMessage("inputs must be a JSON object of at most 32 KB.");
    // TODO(hardening): per-feature allow-list of input field names (PLT-220); only size and shape are checked now.
}

internal sealed class InvokeAiHandler(
    DbAiGate gate, IAiProvider provider, DbSession session, NpgsqlDataSource dataSource, RequestContext context, IClock clock)
    : ICommandHandler<InvokeAi, AiInvokeResponse>
{
    public async Task<Result<AiInvokeResponse>> HandleAsync(InvokeAi command, CancellationToken cancellationToken)
    {
        var featureId = command.Request.FeatureId.Value;
        var inputs = JsonNode.Parse(command.Request.Inputs.GetRawText())!.AsObject();
        var interactionId = Guid.CreateVersion7();

        var before = await gate.CheckAsync(featureId, cancellationToken).ConfigureAwait(false);
        if (!before.IsAllowed)
        {
            await StoreOutsideAsync(interactionId, featureId, inputs, "Refused", before.Reason, cancellationToken).ConfigureAwait(false);
            return Disabled(before.Reason);
        }

        var output = await provider.InvokeAsync(featureId, inputs, cancellationToken).ConfigureAwait(false);

        var after = await gate.CheckFreshAsync(featureId, cancellationToken).ConfigureAwait(false);
        if (!after.IsAllowed)
        {
            await StoreOutsideAsync(interactionId, featureId, inputs, "Discarded", after.Reason, cancellationToken).ConfigureAwait(false);
            return Disabled(after.Reason);
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO plt.ai_interaction (interaction_id, feature_id, legal_entity, actor_kind, actor_id, status, inputs, output, created_at) "
            + "VALUES (@id, @f, @le, @k, @a, 'Suggested', @inputs, @output, @now)", session.Connection, session.Transaction);
        Bind(insert, interactionId, featureId, inputs);
        insert.Parameters.Add(new NpgsqlParameter("output", NpgsqlDbType.Jsonb) { Value = output.ToJsonString() });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        context.AiInteractionId = new AiInteractionId(interactionId);
        return new AiInvokeResponse
        {
            Output = JsonSerializer.SerializeToElement(output),
            InteractionId = interactionId,
            Outcome = AiInvokeResponse.OutcomeValue.Suggested,
        };
    }

    private static Result<AiInvokeResponse> Disabled(string? reason) =>
        DomainError.Of(ModuleCode.PLT, AiErrors.Disabled, $"AI is not available for this feature right now ({reason}); continue on the manual path.");

    private async Task StoreOutsideAsync(Guid id, string featureId, JsonObject inputs, string status, string? reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO plt.ai_interaction (interaction_id, feature_id, legal_entity, actor_kind, actor_id, status, refusal_reason, inputs, created_at) "
            + "VALUES (@id, @f, @le, @k, @a, @status, @reason, @inputs, @now)", connection);
        Bind(insert, id, featureId, inputs);
        insert.Parameters.AddWithValue("status", status);
        insert.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Bind(NpgsqlCommand command, Guid id, string featureId, JsonObject inputs)
    {
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("f", featureId);
        command.Parameters.Add(new NpgsqlParameter("le", NpgsqlDbType.Text) { Value = (object?)context.LegalEntity?.Value ?? DBNull.Value });
        command.Parameters.AddWithValue("k", context.Actor.KindCode);
        command.Parameters.AddWithValue("a", context.Actor.Id);
        command.Parameters.Add(new NpgsqlParameter("inputs", NpgsqlDbType.Jsonb) { Value = inputs.ToJsonString() });
        command.Parameters.AddWithValue("now", clock.Now.ToUtcDateTime());
    }
}

internal sealed class InvokeAiAuditor : ICommandAuditor<InvokeAi, AiInvokeResponse>
{
    public CommandAuditFacts Describe(InvokeAi command, Result<AiInvokeResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.PLT, "AiFeature", command.Request.FeatureId.Value),
        BusinessKeys = BusinessKeys.Empty.With("featureId", command.Request.FeatureId.Value),
    };
}

/// <summary>plt.Ai.recordOutcome: the human decision on a suggestion (accepted, rejected or modified); recorded once.</summary>
internal sealed record RecordAiOutcome(AiRecordOutcomeRequest Request) : ICommand<AiRecordOutcomeResponse>;

internal sealed class RecordAiOutcomeHandler(DbSession session, RequestContext context, IClock clock) : ICommandHandler<RecordAiOutcome, AiRecordOutcomeResponse>
{
    public async Task<Result<AiRecordOutcomeResponse>> HandleAsync(RecordAiOutcome command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var outcome = request.Outcome.ToString();
        await using var update = new NpgsqlCommand(
            "UPDATE plt.ai_interaction SET outcome = @o, outcome_comment = @c, outcome_by_kind = @k, outcome_by_id = @a, outcome_at = @now "
            + "WHERE interaction_id = @id AND status = 'Suggested' AND outcome IS NULL", session.Connection, session.Transaction);
        update.Parameters.AddWithValue("o", outcome);
        update.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = (object?)request.Comment ?? DBNull.Value });
        update.Parameters.AddWithValue("k", context.Actor.KindCode);
        update.Parameters.AddWithValue("a", context.Actor.Id);
        update.Parameters.AddWithValue("now", clock.Now.ToUtcDateTime());
        update.Parameters.AddWithValue("id", request.InteractionId);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            return new AiRecordOutcomeResponse { InteractionId = request.InteractionId, Outcome = outcome };
        }

        await using var read = new NpgsqlCommand("SELECT status, outcome FROM plt.ai_interaction WHERE interaction_id = @id", session.Connection, session.Transaction);
        read.Parameters.AddWithValue("id", request.InteractionId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "The AI interaction does not exist.");
        }

        return reader.IsDBNull(1) || reader.GetString(1) != outcome
            ? DomainError.Of(ModuleCode.PLT, PlatformErrors.InvalidStateTransition, "The interaction is not a suggestion awaiting a decision, or already has a different outcome.")
            : new AiRecordOutcomeResponse { InteractionId = request.InteractionId, Outcome = outcome };
    }
}

internal sealed class RecordAiOutcomeAuditor : ICommandAuditor<RecordAiOutcome, AiRecordOutcomeResponse>
{
    public CommandAuditFacts Describe(RecordAiOutcome command, Result<AiRecordOutcomeResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.PLT, "AiInteraction", command.Request.InteractionId.ToString()),
        Changes = result is { IsSuccess: true } ? [new AuditChange("outcome", null, JsonValue.Create(command.Request.Outcome.ToString()))] : [],
    };
}
