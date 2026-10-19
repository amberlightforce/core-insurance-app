using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.SharedKernel.Ai;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Platform.Ai;

/// <summary>Settings of the AI gate (<c>Platform:Ai</c>).</summary>
public sealed class AiGateOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Platform:Ai";

    /// <summary>Gate cache lifetime in seconds (D-SL6-08: at most 5, so a kill switch is complete in well under 60 s).</summary>
    public int CacheSeconds { get; set; } = 5;
}

/// <summary>
/// Register lookup (D-SL6-08): the status of an AI feature in the compliance AI register (CMP-AIREG supplies the real
/// implementation). The default denies everything: no register means no feature can be enabled (fail closed).
/// </summary>
public interface IAiRegisterLookup
{
    /// <summary>The register status (<c>APPROVED</c> and so on) of the feature, or null when it is not in the register or unreadable.</summary>
    Task<string?> GetStatusAsync(string featureId, CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IAiRegisterLookup"/>: nothing is registered, nothing can be enabled.</summary>
internal sealed class DenyAllAiRegisterLookup : IAiRegisterLookup
{
    public Task<string?> GetStatusAsync(string featureId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>The AI register status that allows a feature to be switched on.</summary>
public static class AiRegisterStatuses
{
    /// <summary>Approved in the register.</summary>
    public const string Approved = "APPROVED";
}

/// <summary>Pluggable AI provider behind <c>plt.Ai.invoke</c> (D-SL6-09: the default is a deterministic fake).</summary>
public interface IAiProvider
{
    /// <summary>Produces a suggestion for the allow-listed inputs.</summary>
    Task<JsonObject> InvokeAsync(string featureId, JsonObject inputs, CancellationToken cancellationToken);
}

/// <summary>Deterministic fake provider: the same inputs always give the same output; no network, no metered calls.</summary>
internal sealed class FakeAiProvider : IAiProvider
{
    public Task<JsonObject> InvokeAsync(string featureId, JsonObject inputs, CancellationToken cancellationToken)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs.ToJsonString()))).ToLowerInvariant();
        return Task.FromResult(new JsonObject
        {
            ["provider"] = "fake",
            ["featureId"] = featureId,
            ["suggestion"] = "DETERMINISTIC_FAKE",
            ["inputDigest"] = digest,
        });
    }
}

/// <summary>
/// The database-backed <see cref="IAiGate"/>: global kill switch, feature kill switch and feature toggle read in one
/// statement and cached per feature for <see cref="AiGateOptions.CacheSeconds"/>. Unreadable state is Disabled and is
/// not cached. <see cref="Invalidate"/> (called after every change in this process and by the event consumers) drops the cache.
/// </summary>
public sealed class DbAiGate(NpgsqlDataSource dataSource, IOptions<AiGateOptions> options) : IAiGate
{
    private const string Sql =
        "SELECT COALESCE((SELECT active FROM plt.ai_kill_switch WHERE scope = 'GLOBAL'), false), "
        + "COALESCE((SELECT active FROM plt.ai_kill_switch WHERE scope = @f), false), "
        + "(SELECT enabled FROM plt.ai_toggle WHERE feature_id = @f)";

    private readonly ConcurrentDictionary<string, (AiGateDecision Decision, long ExpiresAt)> _cache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public AiGateDecision Check(string featureId)
    {
        if (!AiScopes.IsFeature(featureId))
        {
            return AiGateDecision.Disabled(AiGateReasons.UnknownFeature);
        }

        if (_cache.TryGetValue(featureId, out var hit) && Stopwatch.GetTimestamp() < hit.ExpiresAt)
        {
            return hit.Decision;
        }

        try
        {
            using var connection = dataSource.OpenConnection();
            using var command = Prepare(connection, featureId);
            using var reader = command.ExecuteReader();
            reader.Read();
            return Remember(featureId, Decide(reader.GetBoolean(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetBoolean(2)));
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return AiGateDecision.Disabled(AiGateReasons.StateUnreadable);
        }
    }

    /// <inheritdoc />
    public async Task<AiGateDecision> CheckAsync(string featureId, CancellationToken cancellationToken = default)
    {
        if (!AiScopes.IsFeature(featureId))
        {
            return AiGateDecision.Disabled(AiGateReasons.UnknownFeature);
        }

        if (_cache.TryGetValue(featureId, out var hit) && Stopwatch.GetTimestamp() < hit.ExpiresAt)
        {
            return hit.Decision;
        }

        return await CheckFreshAsync(featureId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the state now, bypassing (and refreshing) the cache; used after a provider call.</summary>
    public async Task<AiGateDecision> CheckFreshAsync(string featureId, CancellationToken cancellationToken = default)
    {
        if (!AiScopes.IsFeature(featureId))
        {
            return AiGateDecision.Disabled(AiGateReasons.UnknownFeature);
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Prepare(connection, featureId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return Remember(featureId, Decide(reader.GetBoolean(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetBoolean(2)));
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return AiGateDecision.Disabled(AiGateReasons.StateUnreadable);
        }
    }

    /// <summary>Drops every cached answer so the next check reads the database.</summary>
    public void Invalidate() => _cache.Clear();

    private static NpgsqlCommand Prepare(NpgsqlConnection connection, string featureId)
    {
        var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("f", featureId);
        return command;
    }

    private static AiGateDecision Decide(bool globalKill, bool featureKill, bool? toggle) =>
        globalKill ? AiGateDecision.Disabled(AiGateReasons.KillSwitch)
        : featureKill ? AiGateDecision.Disabled(AiGateReasons.FeatureKillSwitch)
        : toggle == true ? AiGateDecision.Allowed
        : AiGateDecision.Disabled(AiGateReasons.ToggleOff);

    private AiGateDecision Remember(string featureId, AiGateDecision decision)
    {
        var seconds = Math.Clamp(options.Value.CacheSeconds, 0, 5);
        _cache[featureId] = (decision, Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency));
        return decision;
    }
}
