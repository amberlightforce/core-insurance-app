using System.Collections.Concurrent;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>A test-only module ("widgets", schema <c>tst</c>) that writes a table and publishes an event in one command.</summary>
internal sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "tst";

    public DbSet<Widget> Widgets => Set<Widget>();

    protected override string Schema => SchemaName;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Widget>(entity =>
        {
            entity.ToTable("widget");
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Id).HasColumnName("id");
            entity.Property(w => w.Name).HasColumnName("name");
            entity.Property(w => w.Amount).HasColumnName("amount").HasColumnType("numeric(19,4)");
        });
    }

    /// <summary>DDL (as the migrator) and grants for the test schema.</summary>
    public static string Ddl => """
        CREATE SCHEMA IF NOT EXISTS tst;
        CREATE TABLE IF NOT EXISTS tst.widget (id uuid PRIMARY KEY, name text NOT NULL, amount numeric(19,4) NOT NULL);
        GRANT USAGE ON SCHEMA tst TO app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON tst.widget TO app;
        """;
}

internal sealed class Widget
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Amount { get; set; }
}

internal static class WidgetEvents
{
    public static EventDescriptor WidgetCreated { get; } = new(
        ModuleCode.WRK, EventTypeName.Parse("WidgetCreated"), "1.0", ["Widget"], DataClassification.P0)
    {
        RequiredBusinessKeys = ["widgetId"],
    };
}

internal sealed record WidgetCreatedPayload(Guid WidgetId, string Name, Money Amount);

internal sealed record CreateWidget(string Name, decimal Amount, Guid? AggregateId = null, string Fail = "") : ICommand<WidgetCreatedPayload>;

/// <summary>Writes a widget and publishes WidgetCreated; can fail (business failure) or throw after publishing.</summary>
internal sealed class CreateWidgetHandler(WidgetDbContext db, IEventPublisher events, ExecutionLog log) : ICommandHandler<CreateWidget, WidgetCreatedPayload>
{
    public Task<Result<WidgetCreatedPayload>> HandleAsync(CreateWidget command, CancellationToken cancellationToken)
    {
        log.Executions.Enqueue(command.Name);
        var id = Guid.CreateVersion7();
        db.Widgets.Add(new Widget { Id = id, Name = command.Name, Amount = command.Amount });
        var payload = new WidgetCreatedPayload(id, command.Name, new Money(command.Amount, Currency.EUR));
        var aggregate = (command.AggregateId ?? id).ToString();
        events.Publish(new OutgoingEvent(WidgetEvents.WidgetCreated, "Widget", aggregate, payload, BusinessKeys.Empty.With("widgetId", aggregate)));

        return command.Fail switch
        {
            "result" => Task.FromResult<Result<WidgetCreatedPayload>>(DomainError.Of(ModuleCode.WRK, "WIDGET-REFUSED", "refused on purpose")),
            "throw" => throw new InvalidOperationException("boom"),
            _ => Task.FromResult<Result<WidgetCreatedPayload>>(payload),
        };
    }
}

/// <summary>Records what ran (handlers and commands), per harness.</summary>
internal sealed class ExecutionLog
{
    public ConcurrentQueue<string> Executions { get; } = new();

    public ConcurrentQueue<(string Handler, string AggregateId, long Sequence, EventOrigin Origin)> Deliveries { get; } = new();
}

/// <summary>A configurable event handler: records deliveries, and fails while <see cref="FailWhile"/> says so.</summary>
internal sealed class RecordingHandler(ExecutionLog log, HandlerBehaviour behaviour) : IEventHandler<WidgetCreatedPayload>
{
    public async Task HandleAsync(EventEnvelope envelope, WidgetCreatedPayload payload, CancellationToken cancellationToken)
    {
        if (behaviour.FailWhile(envelope))
        {
            throw new InvalidOperationException($"handler failure for {payload.Name}");
        }

        if (behaviour.Delay > TimeSpan.Zero)
        {
            await Task.Delay(behaviour.Delay, cancellationToken);
        }

        log.Deliveries.Enqueue(("recording", envelope.AggregateId, envelope.AggregateSequence, envelope.Origin));
    }
}

internal sealed class HandlerBehaviour
{
    public Func<EventEnvelope, bool> FailWhile { get; set; } = _ => false;

    public TimeSpan Delay { get; set; }
}

/// <summary>
/// Platform services over a real database: the platform module, the widget test module, a manual clock, the stamp
/// defaults and one recording event handler.
/// </summary>
internal sealed class PlatformHarness : IAsyncDisposable
{
    public const string HandlerName = "WRK.WidgetCreated.Recording";

    private PlatformHarness(ServiceProvider services, ManualClock clock, ExecutionLog log, HandlerBehaviour behaviour)
    {
        Services = services;
        Clock = clock;
        Log = log;
        Behaviour = behaviour;
    }

    public ServiceProvider Services { get; }

    public ManualClock Clock { get; }

    public ExecutionLog Log { get; }

    public HandlerBehaviour Behaviour { get; }

    public static ConfigurationHash Hash { get; } = ConfigurationHash.Parse(new string('c', 64));

    public static async Task<PlatformHarness> CreateAsync(PostgresFixture database, Action<OutboxOptions>? outbox = null, Action<IServiceCollection>? configure = null)
    {
        await database.ExecuteAsMigratorAsync(WidgetDbContext.Ddl, CancellationToken.None);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Stamp:LegalEntity"] = "GR-TEST", ["Stamp:Country"] = "GR" })
            .Build();
        var clock = new ManualClock(Instant.Parse("2026-10-07T08:00:00Z"));
        var log = new ExecutionLog();
        var behaviour = new HandlerBehaviour();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(clock);
        services.AddSingleton(log);
        services.AddSingleton(behaviour);
        services.AddPlatformDataSource(new NpgsqlConnectionStringBuilder(database.AppConnectionString) { MaxPoolSize = 64 }.ConnectionString);
        services.AddPlatformModule(configuration);
        services.AddModuleDbContext<WidgetDbContext>(WidgetDbContext.SchemaName);
        services.AddCommand<CreateWidget, WidgetCreatedPayload, CreateWidgetHandler>(new CommandDescriptor(OperationName.Parse("wrk.Widget.create"), ModuleCode.WRK)
        {
            SupportsDryRun = true,
        });
        services.AddEventHandler<WidgetCreatedPayload, RecordingHandler>(WidgetEvents.WidgetCreated, HandlerName, ModuleCode.WRK);
        services.Configure<OutboxOptions>(options =>
        {
            options.MaxDegreeOfParallelism = 8;
            outbox?.Invoke(options);
        });
        configure?.Invoke(services);
        return new PlatformHarness(services.BuildServiceProvider(validateScopes: true), clock, log, behaviour);
    }

    /// <summary>Runs a command in a fresh scope as <paramref name="actor"/>.</summary>
    public async Task<Result<WidgetCreatedPayload>> SendAsync(CreateWidget command, IdempotencyKey? key = null, bool dryRun = false, string actor = "user-1")
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User(actor);
        context.Roles = ["Tester"];
        context.ConfigurationHash = Hash;
        context.IdempotencyKey = key ?? IdempotencyKey.New();
        context.DryRun = dryRun;
        context.LegalEntity = LegalEntityId.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWidget, WidgetCreatedPayload>>()
            .HandleAsync(command, TestContext.Current.CancellationToken);
    }

    public OutboxProcessor Processor => Services.GetRequiredService<OutboxProcessor>();

    public NpgsqlDataSource DataSource => Services.GetRequiredService<NpgsqlDataSource>();

    public async Task<long> CountAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
