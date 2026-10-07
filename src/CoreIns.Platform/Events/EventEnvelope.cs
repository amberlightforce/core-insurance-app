using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Platform.Events;

/// <summary>
/// The static description of one catalogued event type and major version (contracts/events catalogue entry):
/// what every envelope of this type must carry. Owner modules declare one per event (F-1c C# records follow it).
/// </summary>
/// <param name="Producer">The only producing module.</param>
/// <param name="EventType">PascalCase event name.</param>
/// <param name="SchemaVersion">Schema <c>major.minor</c> the producer emits.</param>
/// <param name="AggregateTypes">Allowed aggregate types (ordering key owner).</param>
/// <param name="DataClassification">Pinned highest personal-data class of the payload.</param>
public sealed record EventDescriptor(
    ModuleCode Producer,
    EventTypeName EventType,
    string SchemaVersion,
    IReadOnlyList<string> AggregateTypes,
    DataClassification DataClassification)
{
    /// <summary>Lineage keys each envelope must carry (catalogue <c>x-business-keys</c>; <c>a|b</c> = at least one).</summary>
    public IReadOnlyList<string> RequiredBusinessKeys { get; init; } = [];

    /// <summary>True when the D4 set fields (<c>set_id</c>, <c>set_size</c>, <c>index</c>) are required.</summary>
    public bool SetCompleteness { get; init; }

    /// <summary>Schema major (from <see cref="SchemaVersion"/>).</summary>
    public int Major => int.Parse(SchemaVersion.AsSpan(0, SchemaVersion.IndexOf('.', StringComparison.Ordinal)), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Registry name <c>&lt;producer lower-case&gt;.&lt;EventType&gt;</c> (XMR-F-121), e.g. <c>pol.PolicyBound</c>.</summary>
    public string RegistryName => $"{Producer.ToLowerCode()}.{EventType}";

    /// <summary>Handler routing key: registry name plus major, e.g. <c>pol.PolicyBound.v1</c>.</summary>
    public string RoutingKey => EventRouting.Key(Producer.ToString(), EventType.Value, Major);

    /// <summary>
    /// The descriptor of a generated contract event (<c>XxxV1.Descriptor</c>, D-CON-34): modules publish the generated
    /// payload records with <c>EventDescriptor.From(PartyCreatedV1.Descriptor)</c> instead of hand-writing descriptors.
    /// </summary>
    public static EventDescriptor From(CoreIns.Platform.Contracts.Events.EventContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new EventDescriptor(contract.Producer, EventTypeName.Parse(contract.EventType), contract.SchemaVersion, contract.AggregateTypes, contract.DataClassification)
        {
            RequiredBusinessKeys = contract.RequiredBusinessKeys,
            SetCompleteness = contract.SetCompleteness,
        };
    }
}

/// <summary>Routing keys of event handlers.</summary>
public static class EventRouting
{
    /// <summary><c>&lt;producer lower&gt;.&lt;eventType&gt;.v&lt;major&gt;</c>.</summary>
    public static string Key(string producer, string eventType, int major) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{producer.ToLowerInvariant()}.{eventType}.v{major}");
}

/// <summary>
/// D4 set completeness (D-CON-19): this event is member <see cref="Index"/> of <see cref="Size"/> in set
/// <see cref="SetId"/>. The three fields come together and <c>1 ≤ index ≤ set_size</c> is enforced at construction
/// (contracts/README: the C# envelope rejects a bad set before the outbox row is written).
/// </summary>
public sealed record EventSet
{
    /// <summary>Creates the set membership; throws when <c>index</c> is outside <c>1..size</c>.</summary>
    public EventSet(Guid setId, int size, int index)
    {
        if (setId == Guid.Empty)
        {
            throw new ArgumentException("set_id is required.", nameof(setId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        if (index < 1 || index > size)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"index must satisfy 1 <= index <= set_size ({size}).");
        }

        SetId = setId;
        Size = size;
        Index = index;
    }

    /// <summary>The set.</summary>
    public Guid SetId { get; }

    /// <summary>Number of members.</summary>
    public int Size { get; }

    /// <summary>1-based position.</summary>
    public int Index { get; }
}

/// <summary>
/// The standard envelope of every event (contracts/events/envelope.schema.json; contract §3.4.1; D-CON-01/09/19/26/28).
/// <see cref="Validate"/> enforces what the JSON Schema cannot (set fields together and <c>1 ≤ index ≤ set_size</c>,
/// required business keys, pinned classification) as well as the schema's own rules, so an invalid envelope is
/// rejected before its outbox row is written.
/// </summary>
public sealed partial record EventEnvelope
{
    /// <summary>UUIDv7; consumers are idempotent on it.</summary>
    public required EventId EventId { get; init; }

    /// <summary>Catalogued name.</summary>
    public required EventTypeName EventType { get; init; }

    /// <summary><c>major.minor</c>.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Producing module.</summary>
    public required ModuleCode Producer { get; init; }

    /// <summary>Aggregate type (ordering key owner).</summary>
    public required string AggregateType { get; init; }

    /// <summary>Ordering key.</summary>
    public required string AggregateId { get; init; }

    /// <summary>Gap-free per-aggregate sequence (assigned by the outbox when the event is written; 0 before that).</summary>
    public long AggregateSequence { get; init; }

    /// <summary>Business time of the fact.</summary>
    public required Instant OccurredAt { get; init; }

    /// <summary>Commit time of the fact.</summary>
    public required Instant RecordedAt { get; init; }

    /// <summary>Legal entity code.</summary>
    public required LegalEntityCode LegalEntity { get; init; }

    /// <summary>ISO 3166-1 jurisdiction.</summary>
    public required Jurisdiction Jurisdiction { get; init; }

    /// <summary>Configuration hash in force.</summary>
    public required ConfigurationHash ConfigurationHash { get; init; }

    /// <summary>Lineage keys (never empty).</summary>
    public required BusinessKeys BusinessKeys { get; init; }

    /// <summary>W3C trace id.</summary>
    public required CorrelationId CorrelationId { get; init; }

    /// <summary>Causing event or command, or null.</summary>
    public Guid? CausationId { get; init; }

    /// <summary>Who acted (P1).</summary>
    public required ActorRef Actor { get; init; }

    /// <summary>AI interaction, or null when no AI was involved (always present on the wire, D-CON-26).</summary>
    public AiInteractionId? AiInteractionId { get; init; }

    /// <summary>LIVE, MIGRATION or REPLAY.</summary>
    public required EventOrigin Origin { get; init; }

    /// <summary>Highest personal-data class of the payload.</summary>
    public required DataClassification DataClassification { get; init; }

    /// <summary>D4 set completeness, when the event is part of a set.</summary>
    public EventSet? Set { get; init; }

    /// <summary>The payload object (JSON).</summary>
    public required JsonObject Payload { get; init; }

    /// <summary>Maximum payload size in UTF-8 bytes (PRD-14 §7.1, 256 KB).</summary>
    public const int MaxPayloadBytes = 256 * 1024;

    /// <summary>
    /// Checks the envelope against the contract and, when given, its catalogue descriptor. Returns the problems found
    /// (empty when valid). <paramref name="requireSequence"/> is false before the outbox assigns the sequence.
    /// </summary>
    public IReadOnlyList<string> Validate(EventDescriptor? descriptor = null, bool requireSequence = true)
    {
        var problems = new List<string>();
        if (EventId.Value == Guid.Empty || EventId.Value.Version != 7)
        {
            problems.Add("eventId must be a UUIDv7");
        }

        if (!SchemaVersionPattern().IsMatch(SchemaVersion ?? string.Empty))
        {
            problems.Add("schemaVersion must be major.minor");
        }

        if (!IsName(AggregateType))
        {
            problems.Add("aggregateType must be PascalCase");
        }

        if (AggregateId is not { Length: >= 1 and <= 200 })
        {
            problems.Add("aggregateId must have 1-200 characters");
        }

        if (requireSequence ? AggregateSequence < 1 : AggregateSequence < 0)
        {
            problems.Add("aggregateSequence must be >= 1");
        }

        if (EventType.Value is null || LegalEntity.Value is null || Jurisdiction.Value is null)
        {
            problems.Add("eventType, legalEntity and jurisdiction are required (D-CON-26)");
        }

        if (ConfigurationHash.Hash.Value is null)
        {
            problems.Add("configurationHash is required");
        }

        if (CorrelationId.Value is null)
        {
            problems.Add("correlationId is required");
        }

        if (Actor is null)
        {
            problems.Add("actor is required (D-CON-26)");
        }

        if (BusinessKeys is null || BusinessKeys.IsEmpty)
        {
            problems.Add("businessKeys must contain at least one lineage key (D-CON-28)");
        }

        if (Set is { } set && (set.SetId == Guid.Empty || set.Size < 1 || set.Index < 1 || set.Index > set.Size))
        {
            problems.Add("set fields must satisfy 1 <= index <= set_size with a set_id (contract D4)");
        }

        if (Payload is null)
        {
            problems.Add("payload is required");
        }
        else if (System.Text.Encoding.UTF8.GetByteCount(Payload.ToJsonString()) > MaxPayloadBytes)
        {
            problems.Add("payload exceeds 256 KB");
        }

        if (descriptor is not null)
        {
            CheckDescriptor(descriptor, problems);
        }

        return problems;
    }

    /// <summary>Throws <see cref="EnvelopeValidationException"/> when <see cref="Validate"/> finds problems.</summary>
    public void EnsureValid(EventDescriptor? descriptor = null, bool requireSequence = true)
    {
        var problems = Validate(descriptor, requireSequence);
        if (problems.Count > 0)
        {
            throw new EnvelopeValidationException(EventType.Value ?? "?", problems);
        }
    }

    /// <summary>
    /// The event on the wire (contracts/events/envelope.schema.json): camelCase envelope fields, the D4 set fields under
    /// their contract names <c>set_id</c>, <c>set_size</c>, <c>index</c>, and <c>payload</c>.
    /// </summary>
    public JsonObject ToWireJson()
    {
        var json = new JsonObject
        {
            ["eventId"] = EventId.Value.ToString("D"),
            ["eventType"] = EventType.Value,
            ["schemaVersion"] = SchemaVersion,
            ["producer"] = Producer.ToString(),
            ["aggregateType"] = AggregateType,
            ["aggregateId"] = AggregateId,
            ["aggregateSequence"] = AggregateSequence,
            ["occurredAt"] = OccurredAt.ToString(),
            ["recordedAt"] = RecordedAt.ToString(),
            ["legalEntity"] = LegalEntity.Value,
            ["jurisdiction"] = Jurisdiction.Value,
            ["configurationHash"] = ConfigurationHash.ToString(),
            ["businessKeys"] = JsonSerializer.SerializeToNode(BusinessKeys, SharedKernel.Json.SharedKernelJson.Options),
            ["correlationId"] = CorrelationId.Value,
            ["causationId"] = CausationId?.ToString("D"),
            ["actor"] = new JsonObject { ["kind"] = Actor.KindCode, ["id"] = Actor.Id },
            ["aiInteractionId"] = AiInteractionId?.Value.ToString("D"),
            ["origin"] = Origin.ToCode(),
            ["dataClassification"] = DataClassification.ToString(),
            ["payload"] = Payload.DeepClone(),
        };
        if (Set is { } set)
        {
            json["set_id"] = set.SetId.ToString("D");
            json["set_size"] = set.Size;
            json["index"] = set.Index;
        }

        return json;
    }

    /// <summary>The routing key of this envelope's handlers.</summary>
    public string RoutingKey =>
        EventRouting.Key(Producer.ToString(), EventType.Value, int.Parse(SchemaVersion.AsSpan(0, SchemaVersion.IndexOf('.', StringComparison.Ordinal)), System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Deserialises the payload.</summary>
    public T PayloadAs<T>(JsonSerializerOptions? options = null) =>
        Payload.Deserialize<T>(options ?? SharedKernel.Json.SharedKernelJson.Options)
        ?? throw new JsonException($"The {EventType} payload deserialised to null.");

    private void CheckDescriptor(EventDescriptor descriptor, List<string> problems)
    {
        if (descriptor.Producer != Producer || descriptor.EventType != EventType)
        {
            problems.Add($"envelope {Producer}.{EventType} does not match descriptor {descriptor.Producer}.{descriptor.EventType}");
        }

        if (!string.Equals(descriptor.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            problems.Add($"schemaVersion {SchemaVersion} differs from the catalogued {descriptor.SchemaVersion}");
        }

        if (descriptor.AggregateTypes.Count > 0 && !descriptor.AggregateTypes.Contains(AggregateType, StringComparer.Ordinal))
        {
            problems.Add($"aggregateType {AggregateType} is not one of {string.Join(", ", descriptor.AggregateTypes)}");
        }

        if (descriptor.DataClassification != DataClassification)
        {
            problems.Add($"dataClassification {DataClassification} differs from the pinned {descriptor.DataClassification}");
        }

        if (descriptor.SetCompleteness && Set is null)
        {
            problems.Add("set_id, set_size and index are required for this event (contract D4)");
        }

        if (BusinessKeys is not null)
        {
            foreach (var required in descriptor.RequiredBusinessKeys)
            {
                var alternatives = required.Split('|');
                if (!alternatives.Any(BusinessKeys.ContainsKey))
                {
                    problems.Add($"businessKeys must contain {required} (D-CON-28)");
                }
            }
        }
    }

    private static bool IsName(string? value) => value is { Length: >= 2 and <= 100 } && NamePattern().IsMatch(value);

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaVersionPattern();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>An event envelope broke the contract; nothing was written.</summary>
public sealed class EnvelopeValidationException : InvalidOperationException
{
    /// <summary>Creates the exception for an event type and its problems.</summary>
    public EnvelopeValidationException(string eventType, IReadOnlyList<string> problems)
        : base($"Event {eventType} violates the envelope contract: {string.Join("; ", problems ?? [])}.")
    {
        Problems = problems ?? [];
    }

    /// <summary>Creates the exception.</summary>
    public EnvelopeValidationException()
    {
        Problems = [];
    }

    /// <summary>Creates the exception with a message.</summary>
    public EnvelopeValidationException(string message)
        : base(message)
    {
        Problems = [message];
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public EnvelopeValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [message];
    }

    /// <summary>The problems.</summary>
    public IReadOnlyList<string> Problems { get; }
}
