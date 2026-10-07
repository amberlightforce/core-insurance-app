using System.Text.Json.Nodes;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Platform.Events;

/// <summary>Catalogue entries of the PLT events the platform primitives emit (contracts/events/plt).</summary>
public static class PlatformEvents
{
    /// <summary>The dispatcher's service identity.</summary>
    public const string DispatcherActor = "plt.outbox-dispatcher";

    /// <summary><c>plt.DeadLetterParked</c> v1.0: aggregate Consumer (ordering key consumer_group), P0, business key consumerGroup.</summary>
    public static EventDescriptor DeadLetterParked { get; } = new(
        ModuleCode.PLT, EventTypeName.Parse("DeadLetterParked"), "1.0", ["Consumer"], DataClassification.P0)
    {
        RequiredBusinessKeys = ["consumerGroup"],
    };

    /// <summary>The <c>DeadLetterParked</c> notice for a handler that kept failing on <paramref name="failed"/>.</summary>
    internal static EventEnvelope DeadLetterParkedEnvelope(EventEnvelope failed, string handler, string errorClass, Instant now)
    {
        var envelope = new EventEnvelope
        {
            EventId = EventId.New(),
            EventType = DeadLetterParked.EventType,
            SchemaVersion = DeadLetterParked.SchemaVersion,
            Producer = ModuleCode.PLT,
            AggregateType = "Consumer",
            AggregateId = handler,
            OccurredAt = now,
            RecordedAt = now,
            LegalEntity = failed.LegalEntity,
            Jurisdiction = failed.Jurisdiction,
            ConfigurationHash = failed.ConfigurationHash,
            BusinessKeys = BusinessKeys.Empty.With("consumerGroup", handler),
            CorrelationId = failed.CorrelationId,
            CausationId = failed.EventId.Value,
            Actor = ActorRef.Service(DispatcherActor),
            AiInteractionId = null,
            Origin = Context.EventOrigin.Live,
            DataClassification = DataClassification.P0,
            Payload = new JsonObject
            {
                ["eventType"] = failed.EventType.Value,
                ["consumer"] = handler.Length <= 1024 ? handler : handler[..1024],
                ["errorClass"] = errorClass.Length <= 128 ? errorClass : errorClass[..128],
            },
        };
        envelope.EnsureValid(DeadLetterParked, requireSequence: false);
        return envelope;
    }
}
