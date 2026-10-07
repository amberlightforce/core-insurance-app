using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.Platform.Events;

/// <summary>
/// An in-process consumer of one event type (the "consumer group" of the PRDs, D-ARC-02). Invoked by the outbox
/// dispatcher at least once per event, in aggregate order, inside a transaction that also records
/// <c>plt.processed_event</c> — so a handler that only writes through the scope's <c>DbSession</c> is effectively
/// exactly-once. Handlers must tolerate replays (<c>origin = REPLAY</c>). Events of one aggregate reach all handlers in
/// <c>aggregateSequence</c> order, except a parked event replayed from the dead-letter table, which arrives after later
/// events (D-ARC-26): order-sensitive handlers check the sequence they last applied.
/// </summary>
/// <typeparam name="TPayload">Payload type (deserialised with the SharedKernel JSON options; unknown fields are ignored).</typeparam>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Domain-event handler of the outbox (D-ARC-02 vocabulary), not a .NET event delegate.")]
public interface IEventHandler<in TPayload>
{
    /// <summary>Handles one event.</summary>
    Task HandleAsync(EventEnvelope envelope, TPayload payload, CancellationToken cancellationToken);
}

/// <summary>A registered handler: its unique name (the consumer group), owner module and routing key.</summary>
/// <param name="HandlerName">Unique name, e.g. <c>BIL.PolicyBound.ScheduleCharges</c>; the key of <c>plt.processed_event</c>.</param>
/// <param name="Module">Module that owns the handler (its unit of work).</param>
/// <param name="RoutingKey">Event routing key, e.g. <c>pol.PolicyBound.v1</c>.</param>
/// <param name="Invoke">Resolves the handler in a scope and calls it.</param>
public sealed record EventHandlerRegistration(
    string HandlerName,
    ModuleCode Module,
    string RoutingKey,
    Func<IServiceProvider, EventEnvelope, CancellationToken, Task> Invoke);

/// <summary>All registered handlers, by routing key.</summary>
public sealed partial class EventHandlerRegistry
{
    private readonly Dictionary<string, EventHandlerRegistration[]> _byRoute;

    /// <summary>Builds the registry; handler names must be unique.</summary>
    public EventHandlerRegistry(IEnumerable<EventHandlerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        All = [.. registrations];
        var duplicate = All.GroupBy(r => r.HandlerName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Event handler name '{duplicate.Key}' is registered twice.");
        }

        _byRoute = All.GroupBy(r => r.RoutingKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Every registration.</summary>
    public IReadOnlyList<EventHandlerRegistration> All { get; }

    /// <summary>Handlers of a routing key (empty when none).</summary>
    public IReadOnlyList<EventHandlerRegistration> For(string routingKey) =>
        _byRoute.TryGetValue(routingKey, out var handlers) ? handlers : [];

    /// <summary>A handler by name.</summary>
    public EventHandlerRegistration? Find(string handlerName) =>
        All.FirstOrDefault(r => string.Equals(r.HandlerName, handlerName, StringComparison.Ordinal));

    internal static bool IsValidName(string name) => HandlerNamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{2,199}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HandlerNamePattern();
}

/// <summary>Registration of event handlers.</summary>
public static class EventHandlerRegistrationExtensions
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> for the event type of <paramref name="descriptor"/>. The handler is
    /// resolved per invocation in its own scope.
    /// </summary>
    public static IServiceCollection AddEventHandler<TPayload, THandler>(
        this IServiceCollection services, EventDescriptor descriptor, string handlerName, ModuleCode module)
        where THandler : class, IEventHandler<TPayload>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!EventHandlerRegistry.IsValidName(handlerName))
        {
            throw new ArgumentException($"'{handlerName}' is not a valid handler name.", nameof(handlerName));
        }

        services.TryAddScoped<THandler>();
        services.AddSingleton(new EventHandlerRegistration(
            handlerName,
            module,
            descriptor.RoutingKey,
            (provider, envelope, ct) => provider.GetRequiredService<THandler>().HandleAsync(envelope, envelope.PayloadAs<TPayload>(), ct)));
        return services;
    }
}
