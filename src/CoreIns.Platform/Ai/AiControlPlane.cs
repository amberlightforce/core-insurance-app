using System.Text.Json.Nodes;
using CoreIns.Platform.Approvals;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel.Identifiers;
using Npgsql;

namespace CoreIns.Platform.Ai;

/// <summary>Error names of the AI control plane (PLT-ERR-*, declared in contracts/openapi/plt.yaml).</summary>
internal static class AiErrors
{
    public const string Disabled = "AI-DISABLED";
    public const string NotRegistered = "AI-NOT-REGISTERED";
}

/// <summary>Reason codes carried by <c>AiToggleChanged</c> and <c>AiKillSwitchActivated</c>.</summary>
internal static class AiChangeReasons
{
    public const string Manual = "MANUAL";
    public const string RegisterStatus = "REGISTER_STATUS";
    public const string BiasThreshold = "BIAS_THRESHOLD";
}

/// <summary>Kill switch scopes.</summary>
internal static class AiScopes
{
    public const string Global = "GLOBAL";

    public static bool IsValid(string? scope) => scope == Global || IsFeature(scope);

    public static bool IsFeature(string? value) => AiFeatureId.TryParse(value, out _);
}

/// <summary>
/// Writes of the AI control plane on the scope connection and transaction: toggles, kill switches, the register status
/// last seen, and their events and audit records. Used by the commands and by the event consumers.
/// </summary>
internal sealed class AiControlPlane(DbSession session, RequestContext context, IClock clock, IEventPublisher events, IAuditWriter audit, DbAiGate gate)
{
    public static readonly ActorRef SystemActor = ActorRef.Service("platform-ai");

    private NpgsqlConnection Connection => session.Connection;

    private NpgsqlTransaction Transaction => session.Transaction ?? throw new InvalidOperationException("The AI control plane writes inside a transaction.");

    /// <summary>Sets a toggle; returns the new version and whether the state changed (an unchanged state writes and publishes nothing).</summary>
    public async Task<(bool Changed, long Version)> SetToggleAsync(
        string featureId, bool enabled, ActorRef actor, string eventReason, string? reason, CancellationToken cancellationToken)
    {
        var now = clock.Now.ToUtcDateTime();
        await Execute("INSERT INTO plt.ai_feature (feature_id, created_at) VALUES (@f, @now) ON CONFLICT (feature_id) DO NOTHING",
            cancellationToken, ("f", featureId), ("now", now)).ConfigureAwait(false);

        bool? current = null;
        long version = 0;
        await using (var read = new NpgsqlCommand("SELECT enabled, version FROM plt.ai_toggle WHERE feature_id = @f FOR UPDATE", Connection, Transaction))
        {
            read.Parameters.AddWithValue("f", featureId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                current = reader.GetBoolean(0);
                version = reader.GetInt64(1);
            }
        }

        if (current == enabled)
        {
            return (false, version);
        }

        if (current is null)
        {
            version = 1;
            await Execute(
                "INSERT INTO plt.ai_toggle (feature_id, enabled, version, reason, updated_by_kind, updated_by_id, updated_at) VALUES (@f, @e, 1, @r, @k, @a, @now)",
                cancellationToken, ("f", featureId), ("e", enabled), ("r", reason), ("k", actor.KindCode), ("a", actor.Id), ("now", now)).ConfigureAwait(false);
        }
        else
        {
            version++;
            await Execute(
                "UPDATE plt.ai_toggle SET enabled = @e, version = @v, reason = @r, updated_by_kind = @k, updated_by_id = @a, updated_at = @now WHERE feature_id = @f",
                cancellationToken, ("f", featureId), ("e", enabled), ("v", version), ("r", reason), ("k", actor.KindCode), ("a", actor.Id), ("now", now)).ConfigureAwait(false);
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(AiToggleChangedV1.Descriptor), "AiFeature", featureId,
            new AiToggleChangedV1
            {
                FeatureId = AiFeatureId.Parse(featureId),
                Level = AiToggleChangedV1.LevelValue.Tenant,
                NewState = enabled ? AiToggleChangedV1.NewStateValue.On : AiToggleChangedV1.NewStateValue.Off,
                Actor = ActorOf(actor),
                Reason = eventReason,
            },
            BusinessKeys.Empty.With("featureId", featureId)));
        gate.Invalidate();
        return (true, version);
    }

    /// <summary>Activates or clears a kill switch (scope GLOBAL or a feature id); returns whether the state changed.</summary>
    public async Task<bool> SetKillSwitchAsync(string scope, bool active, ActorRef actor, string? reason, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        bool? current;
        await using (var read = new NpgsqlCommand("SELECT active FROM plt.ai_kill_switch WHERE scope = @s FOR UPDATE", Connection, Transaction))
        {
            read.Parameters.AddWithValue("s", scope);
            current = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as bool?;
        }

        if (current == active || (current is null && !active))
        {
            return false;
        }

        await Execute(
            "INSERT INTO plt.ai_kill_switch (scope, active, version, reason, updated_by_kind, updated_by_id, updated_at) VALUES (@s, @a, 1, @r, @k, @i, @now) "
            + "ON CONFLICT (scope) DO UPDATE SET active = @a, version = plt.ai_kill_switch.version + 1, reason = @r, updated_by_kind = @k, updated_by_id = @i, updated_at = @now",
            cancellationToken, ("s", scope), ("a", active), ("r", reason), ("k", actor.KindCode), ("i", actor.Id), ("now", now.ToUtcDateTime())).ConfigureAwait(false);

        if (active)
        {
            var global = scope == AiScopes.Global;
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(AiKillSwitchActivatedV1.Descriptor), global ? "AiGlobal" : "AiFeature", scope,
                new AiKillSwitchActivatedV1 { Scope = scope, Actor = ActorOf(actor), Reason = AiChangeReasons.Manual, ActivatedAt = now },
                global ? BusinessKeys.Empty.With("killSwitchScope", scope) : BusinessKeys.Empty.With("aiFeatureId", scope)));
        }

        gate.Invalidate();
        return true;
    }

    /// <summary>
    /// Records the register status last seen for a feature. A status other than APPROVED switches that feature off, and
    /// only that feature (REQ-PLT-354); returns true when a toggle was switched off.
    /// </summary>
    public async Task<bool> ApplyRegisterStatusAsync(string featureId, string status, CancellationToken cancellationToken)
    {
        await Execute(
            "INSERT INTO plt.ai_feature (feature_id, register_status, register_status_at, created_at) VALUES (@f, @s, @now, @now) "
            + "ON CONFLICT (feature_id) DO UPDATE SET register_status = @s, register_status_at = @now",
            cancellationToken, ("f", featureId), ("s", status), ("now", clock.Now.ToUtcDateTime())).ConfigureAwait(false);
        return status != AiRegisterStatuses.Approved
            && await DisableAsync(featureId, AiChangeReasons.RegisterStatus, $"Register status {status}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switches a feature off on the authority of the system (a drift, bias or register event) and audits it.</summary>
    public async Task<bool> DisableAsync(string featureId, string eventReason, string reason, CancellationToken cancellationToken)
    {
        var (changed, _) = await SetToggleAsync(featureId, false, SystemActor, eventReason, reason, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            audit.Append(new AuditRecord
            {
                Actor = SystemActor,
                Operation = OperationName.Parse("plt.AiToggle.set"),
                ObjectRef = new ObjectRef(ModuleCode.PLT, "AiFeature", featureId),
                Changes = [new AuditChange("enabled", JsonValue.Create(true), JsonValue.Create(false))],
                Reason = reason,
                CorrelationId = context.CorrelationId,
                CausationId = context.CausationId,
                Origin = context.Origin,
                LegalEntity = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."),
                Jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction."),
                OccurredAt = clock.Now,
                BusinessKeys = BusinessKeys.Empty.With("featureId", featureId),
            });
        }

        return changed;
    }

    private static Actor ActorOf(ActorRef actor) => ApprovalStore.ToActor(actor);

    private async Task Execute(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, Connection, Transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
