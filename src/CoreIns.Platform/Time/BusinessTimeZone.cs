using Microsoft.Extensions.Hosting;

namespace CoreIns.Platform.Time;

/// <summary>
/// Strict lookup of the business time zone (PITFALLS 14). A host without tz data must refuse to run: business dates
/// computed in UTC instead of Europe/Athens are silently wrong around midnight, so there is no UTC fallback anywhere.
/// </summary>
public static class BusinessTimeZone
{
    /// <summary>IANA id of the zone every Greek legal entity runs in.</summary>
    public const string AthensId = "Europe/Athens";

    /// <summary>The Europe/Athens zone; throws (never UTC) when the host lacks tz data.</summary>
    public static TimeZoneInfo Athens => Resolve(AthensId);

    /// <summary>The zone with IANA id <paramref name="id"/>; throws <see cref="InvalidOperationException"/> when the host does not know it.</summary>
    public static TimeZoneInfo Resolve(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"Time zone '{id}' is not available on this host (is tzdata installed? Never falling back to UTC).", ex);
        }
    }

    /// <summary>Fails when the zones the deployment needs are missing; run at startup.</summary>
    public static void EnsureAvailable() => _ = Athens;
}

/// <summary>Fails host startup when the business time zone data is missing (NX-EC-03).</summary>
internal sealed class BusinessTimeZoneStartupCheck : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        BusinessTimeZone.EnsureAvailable();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
