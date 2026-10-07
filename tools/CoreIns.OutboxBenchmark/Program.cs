using System.Diagnostics;
using System.Globalization;
using CoreIns.Platform;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// Outbox throughput benchmark (F-1f b, D8 target >= 2,000 events/s).
//   dotnet run --project tools/CoreIns.OutboxBenchmark -c Release -- --connection "<superuser conn>" [options]
// Options: --events 50000 --aggregates 5000 --handlers 1 --dop 16 --batch 1000 --producers 8 --per-tx 10 --handler noop|write
//          --handler-batch 100 (1 = one transaction per event and handler) --keep
var options = BenchmarkOptions.Parse(args);
var database = "coreins_bench_" + Guid.NewGuid().ToString("N")[..8];
var admin = new NpgsqlConnectionStringBuilder(options.Connection) { Database = "postgres" }.ConnectionString;
var target = new NpgsqlConnectionStringBuilder(options.Connection)
{
    Database = database,
    MaxPoolSize = Math.Max(100, (options.Dop * 2) + options.Producers + 10),
}.ConnectionString;

await using (var server = NpgsqlDataSource.Create(admin))
await using (var create = server.CreateCommand($"CREATE DATABASE {database}"))
{
    await create.ExecuteNonQueryAsync();
}

try
{
    await using (var context = PlatformModule.Databases[0].CreateForMigration(target))
    {
        await context.Database.MigrateAsync();
    }

    await using (var setup = NpgsqlDataSource.Create(target))
    await using (var sink = setup.CreateCommand("CREATE SCHEMA bench; CREATE TABLE bench.sink (handler text, event_id uuid, PRIMARY KEY (handler, event_id))"))
    {
        await sink.ExecuteNonQueryAsync();
    }

    var services = BuildServices(target, options);
    await using (services)
    {
        var version = await ServerVersionAsync(target);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"PostgreSQL: {version}; events={options.Events}, aggregates={options.Aggregates}, handlers={options.Handlers} ({options.Handler}), dop={options.Dop}, batch={options.Batch}, handler-batch={options.HandlerBatch}, producers={options.Producers}, per-tx={options.PerTransaction}"));

        var produce = await ProduceAsync(services, options);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"produce : {options.Events} events in {produce.TotalSeconds:F2} s = {options.Events / produce.TotalSeconds:F0} events/s (outbox insert + sequence, committed)"));

        var processor = services.GetRequiredService<OutboxProcessor>();
        var watch = Stopwatch.StartNew();
        var result = await processor.DrainAsync(CancellationToken.None);
        watch.Stop();
        var deliveries = (long)result.Dispatched * options.Handlers;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"dispatch: {result.Dispatched} events ({deliveries} handler deliveries) in {watch.Elapsed.TotalSeconds:F2} s = {result.Dispatched / watch.Elapsed.TotalSeconds:F0} events/s, {deliveries / watch.Elapsed.TotalSeconds:F0} deliveries/s (claimed {result.Claimed}, released {result.Released}, retried {result.Retried})"));

        await using var check = NpgsqlDataSource.Create(target);
        await using var pending = check.CreateCommand("SELECT count(*) FROM plt.outbox_message WHERE status = 'Pending'");
        Console.WriteLine($"pending after drain: {await pending.ExecuteScalarAsync()}");
    }
}
finally
{
    NpgsqlConnection.ClearAllPools();
    if (!options.Keep)
    {
        await using var server = NpgsqlDataSource.Create(admin);
        await using var drop = server.CreateCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }
    else
    {
        Console.WriteLine($"kept database {database}");
    }
}

static ServiceProvider BuildServices(string connectionString, BenchmarkOptions options)
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Stamp:LegalEntity"] = "GR-BENCH", ["Stamp:Country"] = "GR" })
        .Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddPlatformDataSource(connectionString);
    services.AddPlatformModule(configuration);
    services.Configure<OutboxOptions>(o =>
    {
        o.BatchSize = options.Batch;
        o.MaxDegreeOfParallelism = options.Dop;
        o.HandlerBatchSize = options.HandlerBatch;
    });
    services.AddSingleton(options);
    for (var i = 0; i < options.Handlers; i++)
    {
        services.AddSingleton(new EventHandlerRegistration(
            $"BENCH.Handler{i}",
            ModuleCode.PLT,
            BenchEvents.Recorded.RoutingKey,
            (provider, envelope, ct) => provider.GetRequiredService<SinkHandler>().HandleAsync(envelope, ct)));
    }

    services.AddScoped<SinkHandler>();
    return services.BuildServiceProvider();
}

static async Task<TimeSpan> ProduceAsync(ServiceProvider services, BenchmarkOptions options)
{
    var aggregates = Enumerable.Range(0, options.Aggregates).Select(_ => Guid.CreateVersion7().ToString()).ToArray();
    var transactions = (options.Events + options.PerTransaction - 1) / options.PerTransaction;
    var next = -1;
    var hash = ConfigurationHash.Parse(new string('b', 64));
    var watch = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, options.Producers).Select(_ => Task.Run(async () =>
    {
        while (true)
        {
            var tx = Interlocked.Increment(ref next);
            if (tx >= transactions)
            {
                return;
            }

            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.Actor = ActorRef.Service("bench.producer");
            context.ConfigurationHash = hash;
            var session = scope.ServiceProvider.GetRequiredService<DbSession>();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
            var count = Math.Min(options.PerTransaction, options.Events - (tx * options.PerTransaction));
            for (var i = 0; i < count; i++)
            {
                var number = (tx * options.PerTransaction) + i;
                var aggregate = aggregates[number % aggregates.Length];
                publisher.Publish(new OutgoingEvent(
                    BenchEvents.Recorded, "Bench", aggregate, new { number, note = "benchmark" }, BusinessKeys.Empty.With("benchId", aggregate)));
            }

            await transaction.CommitAsync(CancellationToken.None);
        }
    })));
    watch.Stop();
    return watch.Elapsed;
}

static async Task<string> ServerVersionAsync(string connectionString)
{
    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    await using var command = dataSource.CreateCommand("SHOW server_version");
    return (string)(await command.ExecuteScalarAsync())!;
}

/// <summary>The benchmark's event type.</summary>
internal static class BenchEvents
{
    public static EventDescriptor Recorded { get; } = new(
        ModuleCode.PLT, EventTypeName.Parse("BenchmarkEventRecorded"), "1.0", ["Bench"], DataClassification.P0)
    {
        RequiredBusinessKeys = ["benchId"],
    };
}

/// <summary>A handler that does nothing (noop) or writes one row in its transaction (write).</summary>
internal sealed class SinkHandler(DbSession session, BenchmarkOptions options)
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (options.Handler != "write")
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            "INSERT INTO bench.sink (handler, event_id) VALUES ('h', @id) ON CONFLICT DO NOTHING", session.Connection, session.Transaction);
        command.Parameters.AddWithValue("id", envelope.EventId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Command-line options.</summary>
internal sealed record BenchmarkOptions(
    string Connection, int Events, int Aggregates, int Handlers, int Dop, int Batch, int Producers, int PerTransaction, string Handler, bool Keep, int HandlerBatch)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        int Number(string name, int fallback) => Value(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

        var connection = Value("--connection") ?? Environment.GetEnvironmentVariable("COREINS_TEST_POSTGRES")
            ?? throw new ArgumentException("Pass --connection <superuser connection string> or set COREINS_TEST_POSTGRES.");
        return new BenchmarkOptions(
            connection,
            Number("--events", 50_000),
            Number("--aggregates", 5_000),
            Number("--handlers", 1),
            Number("--dop", 16),
            Number("--batch", 1_000),
            Number("--producers", 8),
            Number("--per-tx", 10),
            Value("--handler") ?? "noop",
            args.Contains("--keep"),
            Number("--handler-batch", 100));
    }
}
