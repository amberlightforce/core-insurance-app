using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CoreIns.Platform.Telemetry;

/// <summary>
/// Business and operational metrics (NX-OT-07) on one <see cref="Meter"/>, exported by the OpenTelemetry pipeline the host wires
/// (Application Insights or OTLP). Tags are low-cardinality names only: never ids, keys or amounts.
/// </summary>
public static class CoreInsMetrics
{
    /// <summary>Meter name the host subscribes to.</summary>
    public const string MeterName = "CoreIns.Business";

    private static readonly Meter Meter = new(MeterName, "1.0");
    private static readonly ConcurrentDictionary<string, long> LastSuccess = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> LastAttempt = new(StringComparer.Ordinal);

    private static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>("coreins.outbox.dead_letters", "{message}", "Outbox handler failures parked in the dead-letter table.");
    private static readonly Histogram<long> OutboxLag = Meter.CreateHistogram<long>("coreins.outbox.lag", "s", "Age of the oldest message in each claimed outbox batch.");
    private static readonly Counter<long> SweeperRuns = Meter.CreateCounter<long>("coreins.sweeper.runs", "{run}", "Sweeper passes by outcome (ok or failed).");
    private static readonly Counter<long> IdempotencyConflicts = Meter.CreateCounter<long>("coreins.idempotency.conflicts", "{conflict}", "Idempotency-Key reused with a different request.");
    private static readonly Counter<long> PaymentFailures = Meter.CreateCounter<long>("coreins.payment.failures", "{failure}", "Payment commands that ended in a domain error.");

    static CoreInsMetrics()
    {
        Meter.CreateObservableGauge<long>(
            "coreins.sweeper.last_success",
            () => LastSuccess.Select(p => new Measurement<long>(p.Value, new KeyValuePair<string, object?>("sweeper", p.Key))).ToArray(),
            "s",
            "Unix time (seconds) of the last successful pass of each sweeper (heartbeat).");
        Meter.CreateObservableGauge<long>(
            "coreins.sweeper.last_attempt",
            () => LastAttempt.Select(p => new Measurement<long>(p.Value, new KeyValuePair<string, object?>("sweeper", p.Key))).ToArray(),
            "s",
            "Unix time (seconds) of the last pass of each sweeper, successful or not.");
    }

    /// <summary>Names of the sweepers and the time of their last successful pass; used by the health check.</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> SweeperHeartbeats =>
        LastAttempt.ToDictionary(p => p.Key, p => DateTimeOffset.FromUnixTimeSeconds(p.Value), StringComparer.Ordinal);

    /// <summary>A sweeper pass completed.</summary>
    public static void SweeperSucceeded(string sweeper, TimeProvider? time = null)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        LastSuccess[sweeper] = now;
        LastAttempt[sweeper] = now;
        SweeperRuns.Add(1, new KeyValuePair<string, object?>("sweeper", sweeper), new KeyValuePair<string, object?>("outcome", "ok"));
    }

    /// <summary>A sweeper pass threw (the loop is alive but the pass failed).</summary>
    public static void SweeperFailed(string sweeper, TimeProvider? time = null)
    {
        LastAttempt[sweeper] = (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        SweeperRuns.Add(1, new KeyValuePair<string, object?>("sweeper", sweeper), new KeyValuePair<string, object?>("outcome", "failed"));
    }

    /// <summary>Handler failures were parked in the dead-letter table.</summary>
    public static void OutboxParked(int count)
    {
        if (count > 0)
        {
            DeadLetters.Add(count);
        }
    }

    /// <summary>The age in seconds of the oldest message of a claimed batch.</summary>
    public static void OutboxBatchLag(long seconds) => OutboxLag.Record(Math.Max(0L, seconds));

    /// <summary>An Idempotency-Key was reused with a different request. <paramref name="source"/> is "command" or "http".</summary>
    public static void IdempotencyConflict(string source) =>
        IdempotencyConflicts.Add(1, new KeyValuePair<string, object?>("source", source));

    /// <summary>A payment command failed. <paramref name="operation"/> is the command name; <paramref name="code"/> the error code.</summary>
    public static void PaymentFailed(string operation, string code) =>
        PaymentFailures.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("code", code));
}
