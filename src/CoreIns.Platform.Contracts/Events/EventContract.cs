using System.Globalization;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;

namespace CoreIns.Platform.Contracts.Events;

/// <summary>
/// A payload record generated from one catalogued event schema (contracts/events/&lt;module&gt;/&lt;Event&gt;.v&lt;major&gt;.schema.json,
/// <c>$defs/Payload</c>). The record carries its catalogue entry as <see cref="Contract"/>.
/// </summary>
public interface IEventPayload
{
    /// <summary>The catalogue entry of this payload's event type and major version.</summary>
    EventContract Contract { get; }
}

/// <summary>
/// A generated payload record with static access to its catalogue entry (for handler registration and publishing
/// without an instance: <c>TPayload.Descriptor</c>).
/// </summary>
/// <typeparam name="TSelf">The payload record.</typeparam>
public interface IEventPayload<TSelf> : IEventPayload
    where TSelf : IEventPayload<TSelf>
{
    /// <summary>The catalogue entry of the event type and major version.</summary>
    static abstract EventContract Descriptor { get; }
}

/// <summary>Whether the PRD defines every payload field (<c>full</c>) or only names (<c>minimal</c>, contracts/README).</summary>
public enum PayloadStatus
{
    /// <summary>Every field of the PRD's payload outline is mapped.</summary>
    Full,

    /// <summary>The PRD gives only names or placeholders; open parts are <c>JsonElement</c> until a minor version defines them.</summary>
    Minimal,
}

/// <summary>
/// The static description of one catalogued event type and major version (contracts/events/catalog.json entry, D-CON-28):
/// what every envelope of the type must carry. Generated for every event; the platform's <c>EventDescriptor</c> is built
/// from it.
/// </summary>
/// <param name="Producer">The only producing module.</param>
/// <param name="EventType">PascalCase event name.</param>
/// <param name="SchemaVersion">Schema <c>major.minor</c> the producer emits.</param>
/// <param name="AggregateTypes">Allowed aggregate types (ordering key owner).</param>
/// <param name="DataClassification">Pinned highest personal-data class of the payload.</param>
public sealed record EventContract(
    ModuleCode Producer,
    string EventType,
    string SchemaVersion,
    IReadOnlyList<string> AggregateTypes,
    DataClassification DataClassification)
{
    /// <summary>Lineage keys each envelope must carry (catalogue <c>x-business-keys</c>; <c>a|b</c> = at least one).</summary>
    public IReadOnlyList<string> RequiredBusinessKeys { get; init; } = [];

    /// <summary>True when the D4 set fields (<c>set_id</c>, <c>set_size</c>, <c>index</c>) are required (D-CON-19).</summary>
    public bool SetCompleteness { get; init; }

    /// <summary>Ordering key named by the catalogue (e.g. <c>policy_id</c>).</summary>
    public string OrderingKey { get; init; } = string.Empty;

    /// <summary>Full or minimal payload.</summary>
    public PayloadStatus PayloadStatus { get; init; }

    /// <summary>Payload fields above P0 (catalogue <c>personalDataFields</c>).</summary>
    public IReadOnlyList<string> PersonalDataFields { get; init; } = [];

    /// <summary>Modules with a declared handler (catalogue <c>consumers</c>, D-CON-09).</summary>
    public IReadOnlyList<ModuleCode> Consumers { get; init; } = [];

    /// <summary>Schema file relative to contracts/events.</summary>
    public string SchemaPath { get; init; } = string.Empty;

    /// <summary>Schema major (from <see cref="SchemaVersion"/>).</summary>
    public int Major => int.Parse(SchemaVersion.AsSpan(0, SchemaVersion.IndexOf('.', StringComparison.Ordinal)), CultureInfo.InvariantCulture);

    /// <summary>Registry name <c>&lt;producer lower-case&gt;.&lt;EventType&gt;</c> (XMR-F-121), e.g. <c>pol.PolicyBound</c>.</summary>
    public string RegistryName => $"{Producer.ToLowerCode()}.{EventType}";

    /// <summary>Event-type namespace <c>&lt;producer lower-case&gt;.events.v&lt;major&gt;</c> (D-ARC-02 "topic").</summary>
    public string Topic => string.Create(CultureInfo.InvariantCulture, $"{Producer.ToLowerCode()}.events.v{Major}");

    /// <summary>Handler routing key <c>&lt;producer lower-case&gt;.&lt;EventType&gt;.v&lt;major&gt;</c>.</summary>
    public string RoutingKey => string.Create(CultureInfo.InvariantCulture, $"{Producer.ToLowerCode()}.{EventType}.v{Major}");
}
