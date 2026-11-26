using System.Diagnostics.Metrics;
using CoreIns.Host.Health;
using CoreIns.Platform.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoreIns.Host.Tests;

public sealed class DependencyHealthTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Blob_uri_is_read_from_an_Azurite_connection_string_or_a_service_uri()
    {
        HttpDependencyHealthCheck.BlobUri(Config(("Blob:ServiceUri", "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=x;BlobEndpoint=http://azurite:10000/devstoreaccount1;")))
            .ShouldBe(new Uri("http://azurite:10000/devstoreaccount1"));
        HttpDependencyHealthCheck.BlobUri(Config(("Blob:ServiceUri", "https://acct.blob.core.windows.net"))).ShouldBe(new Uri("https://acct.blob.core.windows.net"));
        HttpDependencyHealthCheck.BlobUri(Config()).ShouldBeNull();
        HttpDependencyHealthCheck.BlobUri(Config(("Blob:ServiceUri", "not a uri"))).ShouldBeNull();
    }

    [Fact]
    public void Gotenberg_is_probed_at_its_health_path()
    {
        HttpDependencyHealthCheck.GotenbergUri(Config(("DocRender:Url", "http://gotenberg:3000"))).ShouldBe(new Uri("http://gotenberg:3000/health"));
        HttpDependencyHealthCheck.GotenbergUri(Config()).ShouldBeNull();
    }

    [Fact]
    public async Task An_unconfigured_dependency_is_healthy()
    {
        var result = await new HttpDependencyHealthCheck("Gotenberg", null, false, new NoClients()).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task An_unreachable_dependency_is_degraded_not_unhealthy()
    {
        var result = await new HttpDependencyHealthCheck("Gotenberg", new Uri("http://127.0.0.1:1/health"), false, new RealClients())
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        result.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public void Sweeper_heartbeats_are_recorded_and_exported()
    {
        var seen = new List<(string Name, long Value, string? Sweeper)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CoreInsMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            seen.Add((instrument.Name, value, tags.ToArray().FirstOrDefault(t => t.Key == "sweeper").Value as string)));
        listener.Start();

        CoreInsMetrics.SweeperSucceeded("test-sweeper");
        listener.RecordObservableInstruments();

        CoreInsMetrics.SweeperHeartbeats.ShouldContainKey("test-sweeper");
        seen.ShouldContain(m => m.Name == "coreins.sweeper.last_success" && m.Sweeper == "test-sweeper" && m.Value > 0);
    }

    [Fact]
    public void Counters_record_outbox_idempotency_and_payment_events()
    {
        var seen = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CoreInsMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => seen.Add(instrument.Name));
        listener.Start();

        CoreInsMetrics.OutboxParked(2);
        CoreInsMetrics.OutboxBatchLag(2);
        CoreInsMetrics.IdempotencyConflict("http");
        CoreInsMetrics.PaymentFailed("take", "BIL-ERR-CURRENCY");
        CoreInsMetrics.SweeperFailed("test-sweeper");

        seen.ShouldBe(["coreins.outbox.dead_letters", "coreins.outbox.lag", "coreins.idempotency.conflicts", "coreins.payment.failures", "coreins.sweeper.runs"], ignoreOrder: true);
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("not expected");
    }

    private sealed class RealClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
