using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Context;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Platform.Events;

/// <summary>An event a module wants to publish; the platform fills the rest of the envelope from the request context.</summary>
/// <param name="Descriptor">Catalogue entry of the event type.</param>
/// <param name="AggregateType">Aggregate type (one of the descriptor's).</param>
/// <param name="AggregateId">Ordering key.</param>
/// <param name="Payload">Payload object (serialised with <see cref="SharedKernelJson.Options"/>; must serialise to a JSON object).</param>
/// <param name="BusinessKeys">Lineage keys (the descriptor's required keys at least).</param>
public sealed record OutgoingEvent(EventDescriptor Descriptor, string AggregateType, string AggregateId, object Payload, BusinessKeys BusinessKeys)
{
    /// <summary>Business time; defaults to now.</summary>
    public Instant? OccurredAt { get; init; }

    /// <summary>D4 set membership.</summary>
    public EventSet? Set { get; init; }

    /// <summary>Overrides the context's causation id.</summary>
    public Guid? CausationId { get; init; }

    /// <summary>Overrides the context's legal entity.</summary>
    public LegalEntityCode? LegalEntity { get; init; }

    /// <summary>Overrides the context's jurisdiction.</summary>
    public Jurisdiction? Jurisdiction { get; init; }

    /// <summary>Overrides the context's configuration hash (e.g. MKT activation events carry the hash after the change).</summary>
    public ConfigurationHash? ConfigurationHash { get; init; }
}

/// <summary>
/// Publishes events through the transactional outbox (<c>plt.Outbox.publish</c>, ADR §2 rule 7): the envelope is
/// validated now and the row is written in the same database transaction as the domain change, just before it
/// commits. Publishing outside a transaction is a programming error.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Validates and stages the event; returns its envelope (the aggregate sequence is assigned at commit).</summary>
    EventEnvelope Publish(OutgoingEvent outgoing);
}

/// <summary>Stamp defaults (INFRASTRUCTURE §5 <c>Stamp__LegalEntity</c>, <c>Stamp__Country</c>).</summary>
public sealed class StampOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Stamp";

    /// <summary>Legal entity code of the stamp (e.g. GR-TEST).</summary>
    public string? LegalEntity { get; set; }

    /// <summary>ISO 3166-1 country of the stamp (e.g. GR).</summary>
    public string? Country { get; set; }
}

/// <summary>The scoped outbox stage: envelopes published in the current transaction, written before it commits.</summary>
internal sealed class OutboxStaging(IClock clock) : ITransactionParticipant
{
    private readonly List<EventEnvelope> _staged = [];

    public int Order => 100;

    public IReadOnlyList<EventEnvelope> Staged => _staged;

    public void Add(EventEnvelope envelope) => _staged.Add(envelope);

    public async Task BeforeCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        if (_staged.Count == 0)
        {
            return;
        }

        var envelopes = _staged.ToArray();
        _staged.Clear();
        await OutboxWriter.WriteAsync(connection, transaction, envelopes, clock.Now, cancellationToken).ConfigureAwait(false);
    }

    public Task AfterRollbackAsync(DbSession session, CancellationToken cancellationToken)
    {
        _staged.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>Default <see cref="IEventPublisher"/>.</summary>
internal sealed class EventPublisher(
    DbSession session,
    OutboxStaging staging,
    RequestContext context,
    IClock clock,
    IOptions<StampOptions> stamp) : IEventPublisher
{
    public EventEnvelope Publish(OutgoingEvent outgoing)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        if (!session.InTransaction)
        {
            throw new InvalidOperationException(
                "Events are published inside the unit of work's transaction (outbox); begin one first (the command pipeline does).");
        }

        var payload = JsonSerializer.SerializeToNode(outgoing.Payload, outgoing.Payload.GetType(), SharedKernelJson.Options) as JsonObject
            ?? throw new EnvelopeValidationException(outgoing.Descriptor.EventType.Value, ["payload must serialise to a JSON object"]);

        var now = clock.Now;
        var envelope = new EventEnvelope
        {
            EventId = EventId.New(),
            EventType = outgoing.Descriptor.EventType,
            SchemaVersion = outgoing.Descriptor.SchemaVersion,
            Producer = outgoing.Descriptor.Producer,
            AggregateType = outgoing.AggregateType,
            AggregateId = outgoing.AggregateId,
            OccurredAt = outgoing.OccurredAt ?? now,
            RecordedAt = now,
            LegalEntity = outgoing.LegalEntity ?? context.LegalEntity ?? Required<LegalEntityCode>(stamp.Value.LegalEntity, "legal entity"),
            Jurisdiction = outgoing.Jurisdiction ?? context.Jurisdiction ?? Required<Jurisdiction>(stamp.Value.Country, "jurisdiction"),
            ConfigurationHash = outgoing.ConfigurationHash ?? context.ConfigurationHash
                ?? throw new EnvelopeValidationException(outgoing.Descriptor.EventType.Value, ["configurationHash is required: no hash is pinned for this unit of work"]),
            BusinessKeys = outgoing.BusinessKeys,
            CorrelationId = context.CorrelationId,
            CausationId = outgoing.CausationId ?? context.CausationId,
            Actor = context.Actor,
            AiInteractionId = context.AiInteractionId,
            Origin = context.Origin,
            DataClassification = outgoing.Descriptor.DataClassification,
            Set = outgoing.Set,
            Payload = payload,
        };

        envelope.EnsureValid(outgoing.Descriptor, requireSequence: false);
        staging.Add(envelope);
        return envelope;
    }

    private static T Required<T>(string? configured, string what)
        where T : struct, IStringValue<T> =>
        T.TryParse(configured, out var value)
            ? value
            : throw new InvalidOperationException($"No {what} in the request context and no valid stamp default (Stamp section).");
}
