using System.Text.Json.Serialization;
using CoreIns.SharedKernel.Json;

namespace CoreIns.SharedKernel.Identifiers;

/// <summary>Internal id (UUIDv7) of a Activity; owner WRK.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ActivityId>))]
public readonly record struct ActivityId(Guid Value) : IEntityId<ActivityId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static ActivityId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static ActivityId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a Queue; owner WRK.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<QueueId>))]
public readonly record struct QueueId(Guid Value) : IEntityId<QueueId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static QueueId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static QueueId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a work Group; owner WRK.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<WorkGroupId>))]
public readonly record struct WorkGroupId(Guid Value) : IEntityId<WorkGroupId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static WorkGroupId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static WorkGroupId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>Internal id (UUIDv7) of a InboundDocument; owner WRK.</summary>
[JsonConverter(typeof(EntityIdJsonConverter<InboundDocumentId>))]
public readonly record struct InboundDocumentId(Guid Value) : IEntityId<InboundDocumentId>
{
    /// <summary>A new UUIDv7 id.</summary>
    public static InboundDocumentId New() => new(EntityIds.NewGuid());

    /// <summary>Wraps an existing id; the empty GUID is rejected.</summary>
    public static InboundDocumentId From(Guid value) => new(EntityIds.RequireNotEmpty(value));

    /// <summary>The id in lower-case <c>D</c> format.</summary>
    public override string ToString() => Value.ToString("D");
}
