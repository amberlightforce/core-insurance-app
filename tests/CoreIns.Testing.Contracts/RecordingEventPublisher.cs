using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Testing.Contracts;

/// <summary>Builds the platform <see cref="EventDescriptor"/> from a generated payload's catalogue entry.</summary>
public static class EventDescriptors
{
    /// <summary>The descriptor of a generated payload type.</summary>
    public static EventDescriptor For<TPayload>()
        where TPayload : IEventPayload<TPayload> => From(TPayload.Descriptor);

    /// <summary>The descriptor of a catalogue entry.</summary>
    public static EventDescriptor From(EventContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new EventDescriptor(contract.Producer, EventTypeName.Parse(contract.EventType), contract.SchemaVersion,
            contract.AggregateTypes, contract.DataClassification)
        {
            RequiredBusinessKeys = contract.RequiredBusinessKeys,
            SetCompleteness = contract.SetCompleteness,
        };
    }
}

/// <summary>
/// An <see cref="IEventPublisher"/> double (D-PRG-07): builds each envelope as the outbox publisher does (fixed test stamp,
/// gap-free per-aggregate sequence), rejects it when it breaks the envelope contract or its catalogue entry, and — when
/// the payload is a generated contract record or the event type has one — checks that the payload round-trips through
/// that record unchanged. Published envelopes are kept in order.
/// </summary>
public sealed class RecordingEventPublisher : IEventPublisher
{
    private static readonly Lazy<IReadOnlyDictionary<string, Type>> PayloadTypes = new(FindPayloadTypes);

    private readonly ConcurrentQueue<EventEnvelope> _published = new();
    private readonly ConcurrentDictionary<(string, string), long> _sequences = new();
    private readonly Lock _gate = new();

    /// <summary>Legal entity stamped on envelopes (default <c>GR-TEST</c>).</summary>
    public LegalEntityCode LegalEntity { get; init; } = LegalEntityCode.Parse("GR-TEST");

    /// <summary>Jurisdiction stamped on envelopes (default <c>GR</c>).</summary>
    public Jurisdiction Jurisdiction { get; init; } = Jurisdiction.Parse("GR");

    /// <summary>Configuration hash stamped on envelopes.</summary>
    public ConfigurationHash ConfigurationHash { get; init; } = ConfigurationHash.Parse(new string('a', 64));

    /// <summary>Actor stamped on envelopes.</summary>
    public ActorRef Actor { get; init; } = ActorRef.Service("contract-tests");

    /// <summary>Time stamped on envelopes (occurredAt default and recordedAt).</summary>
    public Instant Now { get; set; } = Instant.Parse("2026-10-07T09:00:00Z");

    /// <summary>Every envelope published, in order.</summary>
    public IReadOnlyList<EventEnvelope> Published => [.. _published];

    /// <summary>The payloads of one generated event type, in order.</summary>
    public IReadOnlyList<TPayload> PublishedOf<TPayload>()
        where TPayload : IEventPayload<TPayload> =>
        [.. _published.Where(e => e.RoutingKey == TPayload.Descriptor.RoutingKey).Select(e => e.PayloadAs<TPayload>())];

    /// <summary>Publishes a generated payload with its catalogue descriptor.</summary>
    public EventEnvelope Publish<TPayload>(TPayload payload, string aggregateId, BusinessKeys businessKeys, EventSet? set = null)
        where TPayload : IEventPayload<TPayload> =>
        Publish(new OutgoingEvent(EventDescriptors.For<TPayload>(), TPayload.Descriptor.AggregateTypes[0], aggregateId, payload!, businessKeys) { Set = set });

    /// <inheritdoc />
    public EventEnvelope Publish(OutgoingEvent outgoing)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        var descriptor = outgoing.Descriptor;
        if (outgoing.Payload is IEventPayload typed && typed.Contract.RoutingKey != descriptor.RoutingKey)
        {
            throw new EnvelopeValidationException(descriptor.EventType.Value,
                [$"payload {outgoing.Payload.GetType().Name} belongs to {typed.Contract.RoutingKey}, not {descriptor.RoutingKey}"]);
        }

        var payload = JsonSerializer.SerializeToNode(outgoing.Payload, outgoing.Payload.GetType(), SharedKernelJson.Options) as JsonObject
            ?? throw new EnvelopeValidationException(descriptor.EventType.Value, ["payload must serialise to a JSON object"]);

        lock (_gate)
        {
            var sequence = _sequences.AddOrUpdate((outgoing.AggregateType, outgoing.AggregateId), 1, (_, s) => s + 1);
            var envelope = new EventEnvelope
            {
                EventId = EventId.New(),
                EventType = descriptor.EventType,
                SchemaVersion = descriptor.SchemaVersion,
                Producer = descriptor.Producer,
                AggregateType = outgoing.AggregateType,
                AggregateId = outgoing.AggregateId,
                AggregateSequence = sequence,
                OccurredAt = outgoing.OccurredAt ?? Now,
                RecordedAt = Now,
                LegalEntity = outgoing.LegalEntity ?? LegalEntity,
                Jurisdiction = outgoing.Jurisdiction ?? Jurisdiction,
                ConfigurationHash = outgoing.ConfigurationHash ?? ConfigurationHash,
                BusinessKeys = outgoing.BusinessKeys,
                CorrelationId = CorrelationId.New(),
                CausationId = outgoing.CausationId,
                Actor = Actor,
                AiInteractionId = null,
                Origin = EventOrigin.Live,
                DataClassification = descriptor.DataClassification,
                Set = outgoing.Set,
                Payload = payload,
            };

            try
            {
                envelope.EnsureValid(descriptor);
            }
            catch
            {
                _sequences.AddOrUpdate((outgoing.AggregateType, outgoing.AggregateId), 0, (_, s) => s - 1);
                throw;
            }

            CheckPayload(envelope);
            _published.Enqueue(envelope);
            return envelope;
        }
    }

    /// <summary>Forgets everything published.</summary>
    public void Clear()
    {
        _published.Clear();
        _sequences.Clear();
    }

    private static void CheckPayload(EventEnvelope envelope)
    {
        if (!PayloadTypes.Value.TryGetValue(envelope.RoutingKey, out var type))
        {
            return;
        }

        object? typed;
        try
        {
            typed = envelope.Payload.Deserialize(type, SharedKernelJson.Options);
        }
        catch (JsonException ex)
        {
            throw new EnvelopeValidationException(envelope.EventType.Value, [$"payload does not match {type.Name}: {ex.Message}"]);
        }

        var again = JsonSerializer.SerializeToNode(typed, type, SharedKernelJson.Options);
        if (!JsonNode.DeepEquals(again, envelope.Payload))
        {
            throw new EnvelopeValidationException(envelope.EventType.Value, [$"payload does not round-trip through {type.Name} (unknown or mistyped members)"]);
        }
    }

    private static Dictionary<string, Type> FindPayloadTypes()
    {
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in ContractAssemblies.All.SelectMany(a => a.GetTypes()))
        {
            if (type is { IsClass: true, IsAbstract: false } && typeof(IEventPayload).IsAssignableFrom(type)
                && type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is EventContract contract)
            {
                map[contract.RoutingKey] = type;
            }
        }

        return map;
    }
}

/// <summary>The assemblies holding generated contract types (Platform.Contracts and every module's Contracts).</summary>
public static class ContractAssemblies
{
    /// <summary>All of them, loaded.</summary>
    public static IReadOnlyList<Assembly> All { get; } = Load();

    private static Assembly[] Load()
    {
        string[] modules =
        [
            "Billing", "Channels", "Claims", "Compliance", "Data", "Documents", "Finance", "Market", "Migration", "Party",
            "Policy", "Product", "Rating", "Reinsurance", "Underwriting", "Work",
        ];
        return [typeof(IEventPayload).Assembly, .. modules.Select(m => Assembly.Load($"CoreIns.Modules.{m}.Contracts"))];
    }
}
